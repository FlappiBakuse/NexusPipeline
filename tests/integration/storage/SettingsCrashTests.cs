using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
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
            Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Instance,
                Path.Combine(fixture.Root, "live-diagnostics"), new JournalStartupAdmission(fixture.Instance, new())));
            Assert.Equal(before, Fingerprint(fixture.Instance.RuntimeRoot));
        }
        var stopped = await fixture.Owner.StopAsync();
        Assert.True(stopped.CompleteJobStopped);
        _ = await fixture.Owner.AwaitExitAsync();
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(new(stopped.Identity.ProcessId,
            stopped.Identity.CreatedAtUtc, stopped.Identity.ImagePath, stopped.Identity.ImageSha256, stopped.Identity.UserSid, stopped.Identity.IsElevated)));
        var beforeTask = File.Exists(fixture.TaskPath) ? PhysicalObjectIdentity.Read(fixture.TaskPath) : null;
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "stopped-diagnostics"), new JournalStartupAdmission(fixture.Instance, new()));
        var index = new StartupJournalRecovery(fixture.Instance, new(), database, new(fixture.Root, Path.Combine(fixture.Instance.RuntimeRoot, "extensions"))).Recover(lease);
        var inspector = new ControlledStartupRegistration(fixture.TaskPath)
            { BeforeMutation = () => throw new InvalidOperationException("Recovery must never call an external mutation") };
        for (var round = 0; round < 2; round++)
        {
            StartupRegistrationCoordinator.ReconcileStopped(fixture.Instance, database, lease, index, inspector);
            var change = new HostSettingsChange(enable ? 0 : 1, HostSettings.Default with { AutoStart = enable });
            var result = new HostSettingsStore(database).Commit(HostSettingsStore.Identity(fixture.OperationId, "fixture", change), change, DateTimeOffset.UtcNow);
            Assert.False(result.IsNew);
            Assert.Equal(completes ? StartupEffectState.Completed : StartupEffectState.Quarantined, result.StartupEffect!.State);
            Assert.Equal(enable, new HostSettingsStore(database).Read().Values.AutoStart);
            Assert.Equal(completes ? 0 : 2, index.Current.ResourceHolds.Count);
            Assert.Equal(beforeTask, File.Exists(fixture.TaskPath) ? PhysicalObjectIdentity.Read(fixture.TaskPath) : null);
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
        var databasePath = Path.Combine(fixture.Instance.RuntimeRoot, "state/nexus.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT document FROM startup_registration_intents WHERE operation_id=$id;";
            command.Parameters.AddWithValue("$id", fixture.OperationId.ToString("D"));
            var node = JsonNode.Parse((byte[])command.ExecuteScalar()!)!;
            if (fault == "version") node["version"] = 2;
            else if (fault == "creator") node["creator"]!["processId"] = 0;
            else node["unexpected"] = true;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(node);
            command.CommandText = "UPDATE startup_registration_intents SET document=$document,document_digest=$digest WHERE operation_id=$id;";
            command.Parameters.AddWithValue("$document", bytes);
            command.Parameters.AddWithValue("$digest", Convert.ToHexStringLower(SHA256.HashData(bytes)));
            command.ExecuteNonQuery();
        }
        var before = Fingerprint(fixture.Instance.RuntimeRoot);
        Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "rejected"), new JournalStartupAdmission(fixture.Instance, new())));
        Assert.Equal(before, Fingerprint(fixture.Instance.RuntimeRoot));
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
        Assert.Equal(2, index.Current.ResourceHolds.Count);
    }

    private static async Task<Fixture> Start(StartupEffectBoundary boundary, bool enable)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "startup-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var report = Path.Combine(root, "fault.json");
        var owner = new OwnedCrashProcess();
        try
        {
            owner.Start(["startup-registration", root, boundary.ToString(), enable.ToString(), report], root, Guid.NewGuid(), Guid.NewGuid(), Path.Combine(root, "process.json"));
            var ready = await OwnedCrashProcess.ReadReportAsync(report);
            Assert.Equal("faultReady", ready.GetProperty("status").GetString());
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
