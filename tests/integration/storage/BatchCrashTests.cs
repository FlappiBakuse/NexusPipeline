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
public sealed class BatchCrashTests(ITestOutputHelper output)
{
    [Theory(DisplayName = "Killing The Actual Batch Saving Process Preserves All Old Or All New Snapshots And An Exactly Replayable Operation")]
    [InlineData("Prepared", -1, false)]
    [InlineData("OperationAccepted", -1, false)]
    [InlineData("ItemPublished", 0, false)]
    [InlineData("ItemPublished", 1, false)]
    [InlineData("TransactionCommitted", -1, true)]
    public async Task RecoversAtomicBatches(
        string point, int ordinal, bool committed)
    {
        var fixture = new ConfigurationBatchFixture();
        ConfigurationRevisionBatch initial = new([fixture.Capture("main", "original-main"), fixture.Capture("extra", "original-extra")]);
        fixture.Save(initial);
        ConfigurationRevisionBatch next = new([fixture.Capture("main", "updated-main", 1), fixture.Capture("extra", "updated-extra", 1)]);
        var operation = ConfigurationBatchStore.Identity(Guid.NewGuid(), "fixture", next);
        var input = Path.Combine(fixture.Root, "input.json");
        var report = Path.Combine(fixture.Root, "fault-report.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { runtimeRoot = fixture.Instance.RuntimeRoot,
            diagnosticRoot = Path.Combine(fixture.Root, "writer-diagnostics"), fixtureRoot = fixture.Root, operation, batch = next, point, ordinal }));
        using var saving = new OwnedCrashProcess();
        var identity = saving.Start(["configuration-batch", input, report], fixture.Root, fixture.Instance.InstanceId,
            operation.OperationId, Path.Combine(fixture.Root, "saving-process.json"));
        var fault = await OwnedCrashProcess.ReadReportAsync(report);
        Assert.Equal("faultReady", fault.GetProperty("status").GetString());
        Assert.Equal(point, fault.GetProperty("point").GetString());
        Assert.Equal(ordinal, fault.GetProperty("resourceOrdinal").GetInt32());
        Assert.Equal(identity.ProcessId, fault.GetProperty("processId").GetInt32());
        var stopped = await saving.StopAsync();
        Assert.True(stopped.CompleteJobStopped); Assert.Equal(0u, stopped.Accounting.ActiveProcesses);
        File.WriteAllBytes(Path.Combine(fixture.Root, "stopped-processes.json"), JsonSerializer.SerializeToUtf8Bytes(stopped));
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Root, "recovery-diagnostics"), new JournalStartupAdmission(fixture.Instance, new()));
        var store = new ConfigurationRevisionStore(fixture.Instance, database, new());
        AssertCurrent(store, committed ? next : initial, committed ? 2 : 1);
        var oldContent = ContentReferences(initial).ToHashSet();
        var candidates = oldContent.Concat(ContentReferences(next)).ToHashSet();
        var references = new BlobReferenceIndex(fixture.Instance, new(), database, new(fixture.Instance, new()));
        Assert.Equal((committed ? candidates : oldContent).OrderBy(reference => reference.Sha256, StringComparer.Ordinal),
            references.ReadReferenced(candidates).OrderBy(reference => reference.Sha256, StringComparer.Ordinal));
        var batches = new ConfigurationBatchStore(fixture.Instance, database, new());
        var recovered = batches.Lookup(operation, next);
        if (committed) AssertReplay(recovered, operation.OperationId, next);
        else Assert.Null(recovered);
        using var prepared = batches.Prepare(next);
        var saved = batches.Commit(operation, prepared, DateTimeOffset.UtcNow);
        Assert.Equal(!committed, saved.IsNew);
        var replay = batches.Lookup(operation, next);
        AssertReplay(replay, operation.OperationId, next);
        AssertCurrent(store, next, 2);
        output.WriteLine(JsonSerializer.Serialize(new { point, ordinal, committed, stopped, operationId = operation.OperationId,
            finalRevisions = saved.Items.Select(item => new { item.Saved.Address, item.Saved.Revision }),
            scope = "Actual owned process interruption at immutable preparation and SQL transaction publication boundaries; not physical power loss or external editor restoration" }));
    }

    private static IEnumerable<BlobReference> ContentReferences(ConfigurationRevisionBatch batch) => batch.Changes
        .SelectMany(change => change.Tree.Entries).Where(entry => entry.Kind == FileObjectKind.File)
        .Select(entry => new BlobReference($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));

    private static void AssertCurrent(ConfigurationRevisionStore store, ConfigurationRevisionBatch batch, long revision)
    {
        foreach (var change in batch.Changes) AssertSaved(store.Read(change.Address), change, revision);
    }

    private static void AssertReplay(ConfigurationBatchResult? replay, Guid operationId, ConfigurationRevisionBatch batch)
    {
        Assert.NotNull(replay);
        Assert.False(replay.IsNew); Assert.Equal(operationId, replay.OperationId);
        Assert.Equal(batch.Changes.Select(change => change.Address.ResourceId).Order(StringComparer.Ordinal),
            replay.Items.Select(item => item.Saved.Address.ResourceId).Order(StringComparer.Ordinal));
        foreach (var change in batch.Changes)
        {
            var item = Assert.Single(replay.Items, value => value.Saved.Address == change.Address);
            Assert.False(item.IsNew); Assert.Equal(operationId, item.OperationId);
            AssertSaved(item.Saved, change, change.ExpectedRevision + 1);
        }
    }

    private static void AssertSaved(ConfigurationRevision? saved, ConfigurationRevisionChange change, long revision)
    {
        Assert.NotNull(saved);
        Assert.Equal(change.Address, saved.Address); Assert.Equal(revision, saved.Revision);
        Assert.Equal(change.Contract, saved.Contract); Assert.Equal(change.Tree.Shape, saved.Tree.Shape);
        Assert.Equal(change.Tree.Entries, saved.Tree.Entries);
    }
}
