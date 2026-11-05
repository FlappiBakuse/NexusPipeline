using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class OutputCrashTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Cases()
    {
        yield return ["stopped", 0];
        foreach (var point in Enum.GetValues<ConfigurationOutputBoundary>())
            for (var ordinal = 0; ordinal < 2; ordinal++) yield return [point.ToString(), ordinal];
    }

    [Theory(DisplayName = "Killing The Editor Owner Around Output Publication Uses Durable Job Closure And Only Restores Frozen Known Output")]
    [MemberData(nameof(Cases))]
    public async Task RecoversFrozenOutput(string point, int ordinal)
    {
        var fixture = new ConfigurationBatchFixture();
        ConfigurationRevisionBatch initial = new([fixture.Capture("main", "saved-main"), fixture.Capture("extra", "saved-extra")]); fixture.Save(initial);
        var targets = ImmutableArray.CreateBuilder<ConfigurationTarget>();
        var originals = new Dictionary<string, (FileTreeSnapshot Tree, IReadOnlyList<PhysicalTreeObject> Objects)>();
        foreach (var change in initial.Changes)
        {
            var path = Path.Combine(fixture.Root, change.Address.ResourceId + ".site"); File.WriteAllText(path, "original-" + change.Address.ResourceId);
            var tree = new FileTreeScanner(SnapshotBudget.Default).Capture(path, default); originals[path] = (tree, PhysicalTreeIdentity.Capture(path, tree));
            targets.Add(new ConfigurationTarget(change.Address, 1, change.Contract, path) { WritePolicy = new([""], [FileObjectKind.File]) });
        }
        var request = new ConfigurationEditRequest(1, targets.ToImmutable());
        var operation = ConfigurationEditStore.BeginIdentity(Guid.NewGuid(), "fixture", request);
        var input = Path.Combine(fixture.Root, "editor-owner-input.json"); var report = Path.Combine(fixture.Root, "editor-owner-fault.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { runtimeRoot = fixture.Instance.RuntimeRoot, request, operation, point, ordinal }));
        using var owner = new OwnedCrashProcess();
        var identity = owner.Start(["configuration-editor-session", input, report], fixture.Root, fixture.Instance.InstanceId,
            operation.OperationId, Path.Combine(fixture.Root, "editor-owner-process.json"));
        var fault = await OwnedCrashProcess.ReadReportAsync(report); Assert.Equal("faultReady", fault.GetProperty("status").GetString());
        Assert.Equal(point, fault.GetProperty("point").GetString()); Assert.Equal(identity.ProcessId, fault.GetProperty("processId").GetInt32());
        var stopped = await owner.StopAsync(); Assert.True(stopped.CompleteJobStopped); Assert.Equal(0u, stopped.Accounting.ActiveProcesses);
        File.WriteAllBytes(Path.Combine(fixture.Root, "stopped-editor-owner.json"), JsonSerializer.SerializeToUtf8Bytes(stopped));
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "recovery-diagnostics"), new JournalStartupAdmission(fixture.Instance, new()));
        var recovery = new StartupJournalRecovery(fixture.Instance, new(), database, new(fixture.Root, Path.Combine(fixture.Instance.RuntimeRoot, "extensions")));
        var index = recovery.Recover(lease); var session = new ConfigurationEditStore(fixture.Instance, database, new()).Read(operation.OperationId, "fixture")!;
        var frozenAtFault = point is "JournalFlushed" or "SqlProjected";
        for (var position = 0; position < session.Resources.Length; position++)
        {
            var resource = session.Resources[position];
            var restored = point != "stopped" && (position < ordinal || position == ordinal && frozenAtFault);
            var journal = new RecoveryJournalRegistry(database).ReadSnapshot().Journals.Single(item => item.JournalId == resource.JournalId);
            Assert.Equal(restored ? JournalPhase.Committed : JournalPhase.Quarantined, journal.Phase);
            var target = request.Targets.Single(item => item.Address == resource.Address).TargetPath;
            var originalAt = restored ? target : Path.Combine(Path.GetDirectoryName(target)!, ".nexus-" + resource.JournalId.ToString("D"), "before");
            var actual = new FileTreeScanner(SnapshotBudget.Default).Capture(originalAt, default);
            Assert.Equal(originals[target].Tree.Entries, actual.Entries); Assert.Equal(originals[target].Objects, PhysicalTreeIdentity.Capture(originalAt, actual));
            if (!restored) Assert.Equal("editor-output-" + resource.Address.ResourceId, File.ReadAllText(target));
        }
        Assert.Equal(ConfigurationEditPhase.Editing, session.Phase); Assert.NotEmpty(index.Current.ResourceHolds);
        Assert.All(initial.Changes, change => Assert.Equal(1, fixture.Revisions.Read(change.Address)!.Revision));
        var repeated = recovery.Recover(lease); Assert.Equal(index.Current.Isolations.Count, repeated.Current.Isolations.Count);
        output.WriteLine(JsonSerializer.Serialize(new { point, ordinal, stopped, sourceSession = session.SessionId,
            isolation = repeated.Current.Isolations, holds = repeated.Current.ResourceHolds,
            scope = "Actual stopped editor family and owner process loss; only durably frozen edited scenes restore, remaining bytes and original objects stay isolated" }));
    }
}
