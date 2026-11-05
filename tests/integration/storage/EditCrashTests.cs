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
public sealed class EditCrashTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var shape in new[] { "missing", "file", "directory" })
        {
            yield return [shape, "accepted", 0]; yield return [shape, "editing", 0];
            foreach (var boundary in Enum.GetValues<EditFileBoundary>())
                for (var ordinal = 0; ordinal < 2; ordinal++) yield return [shape, boundary.ToString(), ordinal];
        }
    }

    [Theory(DisplayName = "Killing The Product Edit Executor At Every Install Boundary Recovers Original Objects Or Preserves Explicit Isolation And Session Ownership")]
    [MemberData(nameof(Cases))]
    public async Task RecoversEditBoundaries(
        string shape, string point, int ordinal)
    {
        var fixture = new ConfigurationBatchFixture();
        ConfigurationRevisionBatch initial = new([fixture.Capture("main", "saved-main"), fixture.Capture("extra", "saved-extra")]);
        fixture.Save(initial);
        var targets = ImmutableArray.CreateBuilder<ConfigurationTarget>();
        var originals = new Dictionary<string, (FileTreeSnapshot Tree, IReadOnlyList<PhysicalTreeObject> Objects)>();
        foreach (var change in initial.Changes)
        {
            var path = Path.Combine(fixture.Root, change.Address.ResourceId + ".site");
            if (shape == "file") File.WriteAllText(path, "original-" + change.Address.ResourceId);
            if (shape == "directory") { Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "original.json"), "original-" + change.Address.ResourceId); }
            var tree = new FileTreeScanner(SnapshotBudget.Default).Capture(path, default);
            originals.Add(path, (tree, PhysicalTreeIdentity.Capture(path, tree)));
            targets.Add(new(change.Address, 1, change.Contract, path));
        }
        var request = new ConfigurationEditRequest(1, targets.ToImmutable());
        var operation = ConfigurationEditStore.BeginIdentity(Guid.NewGuid(), "fixture", request);
        var input = Path.Combine(fixture.Root, "edit-input.json"); var report = Path.Combine(fixture.Root, "edit-fault.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { runtimeRoot = fixture.Instance.RuntimeRoot, request, operation, point, ordinal }));
        using var executing = new OwnedCrashProcess();
        var identity = executing.Start(["configuration-edit", input, report], fixture.Root, fixture.Instance.InstanceId,
            operation.OperationId, Path.Combine(fixture.Root, "edit-process.json"));
        var fault = await OwnedCrashProcess.ReadReportAsync(report);
        Assert.Equal("faultReady", fault.GetProperty("status").GetString()); Assert.Equal(point, fault.GetProperty("point").GetString());
        Assert.Equal(identity.ProcessId, fault.GetProperty("processId").GetInt32());
        var stopped = await executing.StopAsync(); Assert.True(stopped.CompleteJobStopped); Assert.Equal(0u, stopped.Accounting.ActiveProcesses);
        File.WriteAllBytes(Path.Combine(fixture.Root, "stopped-edit-family.json"), JsonSerializer.SerializeToUtf8Bytes(stopped));
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "recovery-diagnostics"), new JournalStartupAdmission(fixture.Instance, new()));
        var recovery = new StartupJournalRecovery(fixture.Instance, new(), database, new(fixture.Root, Path.Combine(fixture.Instance.RuntimeRoot, "extensions")));
        var index = recovery.Recover(lease);
        var store = new ConfigurationEditStore(fixture.Instance, database, new());
        var session = store.LookupBegin(operation, request)!;
        var quarantined = point == "WorkspaceCreated"; var editing = point == "editing";
        Assert.Equal(quarantined ? ConfigurationEditPhase.Prepared : editing ? ConfigurationEditPhase.Editing : ConfigurationEditPhase.Cancelled, session.Phase);
        Assert.Equal(quarantined, index.Current.Isolations.Count != 0);
        Assert.Equal(quarantined || editing, index.Current.ResourceHolds.Count != 0);
        var lifecycle = new HostLifecycle(index, TimeProvider.System);
        Assert.Equal(!(quarantined || editing), lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit is not null);
        foreach (var pair in originals)
        {
            var actual = new FileTreeScanner(SnapshotBudget.Default).Capture(pair.Key, default);
            Assert.Equal(pair.Value.Tree.Shape, actual.Shape); Assert.Equal(pair.Value.Tree.Entries, actual.Entries);
            Assert.Equal(pair.Value.Objects, PhysicalTreeIdentity.Capture(pair.Key, actual));
        }
        Assert.All(initial.Changes, change => Assert.Equal(1, fixture.Revisions.Read(change.Address)!.Revision));
        var repeated = recovery.Recover(lease);
        Assert.Equal(session.Phase, store.Read(session.SessionId, "fixture")!.Phase);
        Assert.Equal(quarantined || editing, repeated.Current.ResourceHolds.Count != 0);
        ConfigurationEditInventory.VerifyReadOnly(database);
        output.WriteLine(JsonSerializer.Serialize(new { shape, point, ordinal, stopped, sessionId = session.SessionId,
            phase = session.Phase.ToString(), isolations = repeated.Current.Isolations, holds = repeated.Current.ResourceHolds,
            scope = "Actual product exchange, persistent multi-resource edit and startup recovery under owned process interruption; edited writer output and physical power loss not covered" }));
    }
}
