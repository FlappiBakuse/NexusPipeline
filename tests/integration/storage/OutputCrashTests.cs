using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
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
        var index = recovery.Recover(lease);
        var recovered = index.Current;
        var session = new ConfigurationEditStore(fixture.Instance, database, new()).LookupBegin(operation, request);
        Assert.NotNull(session);
        Assert.Equal(operation.OperationId, session.SessionId);
        Assert.Equal(ConfigurationEditPhase.Editing, session.Phase);
        Assert.Null(session.CompletionOperationId);
        Assert.Equal(request.Targets.Select(target => target.Address.ResourceId).Order(StringComparer.Ordinal),
            session.Resources.Select(resource => resource.Address.ResourceId).Order(StringComparer.Ordinal));
        Assert.Equal(point == "stopped" ? session.SessionId : session.Resources[ordinal].JournalId, fault.GetProperty("id").GetGuid());
        var frozenAtFault = point is "JournalFlushed" or "SqlProjected";
        var registry = new RecoveryJournalRegistry(database);
        var isolated = new HashSet<Guid>();
        for (var position = 0; position < session.Resources.Length; position++)
        {
            var resource = session.Resources[position];
            var restored = point != "stopped" && (position < ordinal || position == ordinal && frozenAtFault);
            var journal = registry.ReadJournal(resource.JournalId);
            Assert.Equal(session.SessionId, journal.OperationId);
            Assert.Equal(restored ? JournalPhase.Committed : JournalPhase.Quarantined, journal.Phase);
            var target = request.Targets.Single(item => item.Address == resource.Address).TargetPath;
            var originalAt = restored ? target : Path.Combine(Path.GetDirectoryName(target)!, ".nexus-" + resource.JournalId.ToString("D"), "before");
            var actual = new FileTreeScanner(SnapshotBudget.Default).Capture(originalAt, default);
            Assert.Equal(originals[target].Tree.Shape, actual.Shape);
            Assert.Equal(originals[target].Tree.Entries, actual.Entries); Assert.Equal(originals[target].Objects, PhysicalTreeIdentity.Capture(originalAt, actual));
            var key = ConfigurationRevisionService.ResourceKey(resource.Address);
            Assert.Contains(recovered.ResourceHolds, hold => hold.OperationId == session.SessionId && hold.ResourceKey == key &&
                hold.Owner == RecoveryHoldOwner.ConfigurationOperations && hold.Mode == ResourceClaimMode.Mutation);
            if (!restored)
            {
                isolated.Add(resource.JournalId);
                Assert.Contains(recovered.Isolations, item => item.JournalId == resource.JournalId && item.ResourceKey == key);
                Assert.Equal("editor-output-" + resource.Address.ResourceId, File.ReadAllText(target));
            }
        }
        Assert.Equal(isolated.Order(), recovered.Isolations.Select(item => item.JournalId).Distinct().Order());
        var revisions = new ConfigurationRevisionStore(fixture.Instance, database, new());
        foreach (var change in initial.Changes)
        {
            var saved = revisions.Read(change.Address)!;
            Assert.Equal(1, saved.Revision); Assert.Equal(change.Contract, saved.Contract);
            Assert.Equal(change.Tree.Shape, saved.Tree.Shape); Assert.Equal(change.Tree.Entries, saved.Tree.Entries);
        }
        var repeated = recovery.Recover(lease);
        Assert.Equal(recovered.Isolations, repeated.Current.Isolations);
        Assert.Equal(recovered.ResourceHolds, repeated.Current.ResourceHolds);
        output.WriteLine(JsonSerializer.Serialize(new { point, ordinal, stopped, sourceSession = session.SessionId,
            isolation = repeated.Current.Isolations, holds = repeated.Current.ResourceHolds,
            scope = "Actual stopped editor family and owner process loss; only durably frozen edited scenes restore, remaining bytes and original objects stay isolated" }));
    }
}
