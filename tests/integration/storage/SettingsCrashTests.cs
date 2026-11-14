using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Settings;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Testing;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class SettingsCrashTests
{
    [Theory(DisplayName = "Startup Recovery Requires The Actual Creator To Stop And Never Repeats The External Effect")]
    [InlineData(StartupEffectBoundary.Saved, true, false)]
    [InlineData(StartupEffectBoundary.SnapshotPublished, true, false)]
    [InlineData(StartupEffectBoundary.Entered, true, false)]
    [InlineData(StartupEffectBoundary.NativeReturned, true, true)]
    [InlineData(StartupEffectBoundary.Completed, true, true)]
    [InlineData(StartupEffectBoundary.Saved, false, false)]
    [InlineData(StartupEffectBoundary.SnapshotPublished, false, false)]
    [InlineData(StartupEffectBoundary.Entered, false, false)]
    [InlineData(StartupEffectBoundary.NativeReturned, false, true)]
    [InlineData(StartupEffectBoundary.Completed, false, true)]
    public async Task RequiresStoppedCreator(
        StartupEffectBoundary point, bool enable, bool completes)
    {
        using var fixture = await Start(point, enable);
        if (point != StartupEffectBoundary.Completed)
        {
            var before = Fingerprint(fixture.Instance.RuntimeRoot);
            Assert.Equal("startup_effect_creator_not_stopped", Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Instance,
                Path.Combine(fixture.Root, "live-diagnostics"), new JournalStartupAdmission(fixture.Instance, new()))).Message);
            Assert.Equal(before, Fingerprint(fixture.Instance.RuntimeRoot));
        }
        var stopped = await fixture.Owner.StopAsync();
        Assert.True(stopped.CompleteJobStopped);
        _ = await fixture.Owner.AwaitExitAsync();
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(new(stopped.Identity.ProcessId,
            stopped.Identity.CreatedAtUtc, stopped.Identity.ImagePath, stopped.Identity.ImageSha256, stopped.Identity.UserSid, stopped.Identity.IsElevated)));
        var beforeTask = File.Exists(fixture.TaskPath) ? PhysicalObjectIdentity.Read(fixture.TaskPath) : null;
        Assert.Equal(enable == completes, beforeTask is not null);
        var beforeBytes = beforeTask is null ? null : File.ReadAllBytes(fixture.TaskPath);
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "stopped-diagnostics"), new JournalStartupAdmission(fixture.Instance, new()));
        var index = new StartupJournalRecovery(fixture.Instance, new(), database, new(fixture.Root, Path.Combine(fixture.Instance.RuntimeRoot, "extensions"))).Recover(lease);
        var inspector = new ControlledStartupRegistration(fixture.TaskPath)
            { BeforeMutation = () => throw new InvalidOperationException("Recovery must never call an external mutation") };
        var state = completes ? StartupEffectState.Completed : StartupEffectState.Quarantined;
        for (var round = 0; round < 2; round++)
        {
            StartupRegistrationCoordinator.ReconcileStopped(fixture.Instance, database, lease, index, inspector);
            AssertRecovered(fixture.OperationId, enable, state, database, index);
            Assert.Equal(beforeTask, File.Exists(fixture.TaskPath) ? PhysicalObjectIdentity.Read(fixture.TaskPath) : null);
            if (beforeBytes is not null) Assert.Equal(beforeBytes, File.ReadAllBytes(fixture.TaskPath));
        }
    }

    [Theory(DisplayName = "Unknown Startup Intent Metadata Rejects The Original Database Read Only")]
    [InlineData("version")]
    [InlineData("creator")]
    [InlineData("extra")]
    public async Task PreservesUnknownIntents(string fault)
    {
        using var fixture = await Start(StartupEffectBoundary.NativeReturned, true);
        Assert.True((await fixture.Owner.StopAsync()).CompleteJobStopped);
        _ = await fixture.Owner.AwaitExitAsync();
        var databasePath = Path.Combine(fixture.Instance.RuntimeRoot, "state/runtime.db");
        using (var connection = NexusPipeline.TestFixtures.DatabaseFixtureConnection.Open(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT document FROM system.startup_registration_intents WHERE operation_id=$id;";
            command.Parameters.AddWithValue("$id", fixture.OperationId.ToString("D"));
            var source = (byte[])command.ExecuteScalar()!;
            var bytes = FaultedIntent(source, fault);
            command.CommandText = "UPDATE system.startup_registration_intents SET document=$document,document_digest=$digest WHERE operation_id=$id;";
            command.Parameters.AddWithValue("$document", bytes);
            command.Parameters.AddWithValue("$digest", Convert.ToHexStringLower(SHA256.HashData(bytes)));
            command.ExecuteNonQuery();
        }
        var before = Fingerprint(fixture.Instance.RuntimeRoot);
        var reason = fault switch
        {
            "version" => "startup_intent_identity_invalid",
            "creator" => "startup_intent_creator_unbound",
            _ => "startup_intent_document_unknown"
        };
        Assert.Equal(reason, Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Instance,
            Path.Combine(fixture.Root, "rejected"), new JournalStartupAdmission(fixture.Instance, new()))).Message);
        Assert.Equal(before, Fingerprint(fixture.Instance.RuntimeRoot));
    }

    private static byte[] FaultedIntent(byte[] source, string fault)
    {
        if (fault == "extra")
        {
            var node = JsonNode.Parse(source)!;
            node["unexpected"] = true;
            return JsonSerializer.SerializeToUtf8Bytes(node);
        }
        var intent = JsonSerializer.Deserialize<StartupIntentDocument>(source, JsonSerializerOptions.Web)!;
        var changed = fault == "version" ? intent with { Version = 2 } : intent with { Creator = intent.Creator with { ProcessId = 0 } };
        return JsonSerializer.SerializeToUtf8Bytes(changed, JsonSerializerOptions.Web);
    }

    [Fact(DisplayName = "A Changed External Startup Registration Is Quarantined Without Touching Its Bytes")]
    public async Task IsolatesChangedRegistration()
    {
        using var fixture = await Start(StartupEffectBoundary.NativeReturned, true);
        Assert.True((await fixture.Owner.StopAsync()).CompleteJobStopped);
        _ = await fixture.Owner.AwaitExitAsync();
        File.WriteAllBytes(fixture.TaskPath, [91, 0, 8]);
        var original = PhysicalObjectIdentity.Read(fixture.TaskPath);
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "diagnostics"), new JournalStartupAdmission(fixture.Instance, new()));
        var index = new StartupJournalRecovery(fixture.Instance, new(), database, new(fixture.Root, Path.Combine(fixture.Instance.RuntimeRoot, "extensions"))).Recover(lease);
        var inspector = new ControlledStartupRegistration(fixture.TaskPath)
            { BeforeMutation = () => throw new InvalidOperationException("Foreign registration must remain untouched") };
        StartupRegistrationCoordinator.ReconcileStopped(fixture.Instance, database, lease, index, inspector);
        Assert.Equal(original, PhysicalObjectIdentity.Read(fixture.TaskPath));
        Assert.Equal(new byte[] { 91, 0, 8 }, File.ReadAllBytes(fixture.TaskPath));
        AssertRecovered(fixture.OperationId, true, StartupEffectState.Quarantined, database, index);
    }

    private static void AssertRecovered(Guid operationId, bool enable, StartupEffectState state, StateDatabase database, RecoveryIndex index)
    {
        var change = new HostSettingsChange(enable ? 0 : 1, HostSettings.Default with { AutoStart = enable });
        var store = new HostSettingsStore(database);
        var identity = HostSettingsStore.Identity(operationId, "fixture", change);
        var expected = new SettingsSaveResult(operationId, new(change.ExpectedRevision + 1, change.Values), false)
            { StartupEffect = new(operationId, change.ExpectedRevision + 1, enable, state) };
        Assert.Equal(expected, store.Commit(identity, change, DateTimeOffset.UtcNow));
        Assert.Equal(expected.Saved, store.Read());
        RecoveryResourceHold[] holds = state == StartupEffectState.Completed ? [] : SettingsStartupTests.StartupHolds(operationId);
        Assert.Equal(holds, index.Current.ResourceHolds);
    }

    private static async Task<Fixture> Start(StartupEffectBoundary boundary, bool enable)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "startup-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var report = Path.Combine(root, "fault.json");
        var owner = new OwnedCrashProcess();
        try
        {
            var identity = owner.Start(["startup-registration", root, boundary.ToString(), enable.ToString(), report],
                root, Guid.NewGuid(), Guid.NewGuid(), Path.Combine(root, "process.json"));
            var ready = await OwnedCrashProcess.ReadReportAsync(report);
            Assert.Equal("faultReady", ready.GetProperty("status").GetString());
            Assert.Equal(identity.ProcessId, ready.GetProperty("processId").GetInt32());
            Assert.Equal(boundary.ToString(), ready.GetProperty("boundary").GetString());
            Assert.Equal(enable, ready.GetProperty("enable").GetBoolean());
            return new(root, ready.GetProperty("instance").Deserialize<RuntimeInstance>()!, ready.GetProperty("operationId").GetGuid(),
                ready.GetProperty("taskPath").GetString()!, owner);
        }
        catch { owner.Dispose(); throw; }
    }

    private static string[] Fingerprint(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order().Select(path =>
        Path.GetRelativePath(root, path) + ":" + (Path.GetFileName(path) == "instance.lock" ? new FileInfo(path).Length.ToString()
            : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();
    private sealed record Fixture(string Root, RuntimeInstance Instance, Guid OperationId, string TaskPath, OwnedCrashProcess Owner) : IDisposable
    { public void Dispose() => Owner.Dispose(); }
}
