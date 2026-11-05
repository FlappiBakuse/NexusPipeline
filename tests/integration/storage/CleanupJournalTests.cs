using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class CleanupJournalTests
{
    [Fact(DisplayName = "A Real Cleanup Journal Deletes Only Its Unreferenced Items And Closes Every Primary Effect")]
    public void ClosesPrimaryEffects()
    {
        var fixture = Create(2);
        var record = RecordStoreFixture.Record(1, Guid.NewGuid());
        new RunRecordStore(fixture.Database).AppendCompleted([record]);
        var retained = fixture.Plan.Items[1].Blob;
        new RecordAssetStore(fixture.Database, fixture.Blobs).AttachVerified(new(record.FinalAttempt.RunId, retained.Sha256, retained.SizeBytes, RecordAssetKind.Screenshot));
        Accept(fixture);
        Assert.Equal(FileCleanupResult.Cleaned, new FileCleanupRecovery(Open(fixture)).Execute());
        Assert.False(File.Exists(Path.Combine(fixture.Instance.RuntimeRoot, fixture.Plan.Items[0].Blob.RelativePath)));
        fixture.Blobs.Verify(retained);
        var snapshot = new RecoveryJournalRegistry(fixture.Database).ReadSnapshot();
        Assert.Empty(snapshot.Isolations);
        Assert.Equal(JournalPhase.Committed, Assert.Single(snapshot.Journals).Phase);
        Assert.Contains(snapshot.FileEffects, effect => effect.State == FileEffectState.Completed);
        Assert.Contains(snapshot.FileEffects, effect => effect.State == FileEffectState.NotExecuted);
        Assert.Equal(FileCleanupResult.AlreadyCompleted, new FileCleanupRecovery(Open(fixture)).Recover());
    }

    public static IEnumerable<object[]> PersistenceBoundaries()
    {
        yield return [CleanupPersistencePoint.AcceptanceCommitted, 1L, JournalRecoveryResult.NotExecuted];
        foreach (var point in new[] { CleanupPersistencePoint.JournalFlushed, CleanupPersistencePoint.SqlProjectionCommitted })
            foreach (var revision in new long[] { 2, 3, 4, 5, 6 })
                yield return [point, revision, revision == 6 ? JournalRecoveryResult.AlreadyCompleted : JournalRecoveryResult.Completed];
        yield return [CleanupPersistencePoint.OutboxCommitted, 3L, JournalRecoveryResult.Completed];
        yield return [CleanupPersistencePoint.OutboxCommitted, 4L, JournalRecoveryResult.Completed];
        yield return [CleanupPersistencePoint.FileRemoved, 3L, JournalRecoveryResult.Quarantined];
    }

    [Theory(DisplayName = "A New Cleanup Session Reconciles Every Recognized Persistence Boundary And Quarantines An Unreceipted Deletion")]
    [MemberData(nameof(PersistenceBoundaries))]
    public void ReconcilesDurableBoundaries(
        CleanupPersistencePoint point, long revision, JournalRecoveryResult expected)
    {
        var fixture = Create();
        var observer = new InterruptAt(point, revision);
        if (point == CleanupPersistencePoint.AcceptanceCommitted)
            Assert.Throws<ControlledInterruption>(() => Accept(fixture, observer));
        else
        {
            Accept(fixture);
            Assert.Throws<ControlledInterruption>(() => new FileCleanupRecovery(Open(fixture, observer)).Execute());
        }
        Assert.True(observer.ReachedBoundary);
        var registry = new RecoveryJournalRegistry(fixture.Database);
        var before = Assert.Single(registry.ReadSnapshot().Journals);
        var handler = new CleanupRecoveryHandler(fixture.Instance, new(), fixture.Database,
            id => new(fixture.Instance.InstanceId, id, true, WriterState.Stopped));
        Assert.Equal(expected, handler.Prepare(before).Execute());
        var after = registry.ReadSnapshot();
        Assert.Equal(expected == JournalRecoveryResult.Quarantined ? JournalPhase.Quarantined : JournalPhase.Committed, Assert.Single(after.Journals).Phase);
        if (expected == JournalRecoveryResult.NotExecuted) fixture.Blobs.Verify(fixture.Plan.Items[0].Blob);
        else Assert.False(File.Exists(Path.Combine(fixture.Instance.RuntimeRoot, fixture.Plan.Items[0].Blob.RelativePath)));
        var files = Fingerprints(fixture.Instance.RuntimeRoot);
        Assert.Equal(expected == JournalRecoveryResult.Quarantined ? JournalRecoveryResult.Quarantined : JournalRecoveryResult.AlreadyCompleted,
            handler.Prepare(Assert.Single(after.Journals)).Execute());
        Assert.Equal(files, Fingerprints(fixture.Instance.RuntimeRoot));
        Assert.Equal(after.Revision, registry.ReadSnapshot().Revision);
    }

    [Theory(DisplayName = "An Interrupted No Effect Cleanup Commit Can Finish Without Deleting Any Content")]
    [InlineData(CleanupPersistencePoint.JournalFlushed)]
    [InlineData(CleanupPersistencePoint.OutboxCommitted)]
    public void CompletesUnexecutedCommit(CleanupPersistencePoint point)
    {
        var fixture = Create(2);
        Accept(fixture);
        var observer = new InterruptAt(point, 2);
        Assert.Throws<ControlledInterruption>(() => new FileCleanupRecovery(Open(fixture, observer)).Recover());
        Assert.True(observer.ReachedBoundary);
        Assert.Equal(FileCleanupResult.NotExecuted, new FileCleanupRecovery(Open(fixture)).Recover());
        Assert.All(fixture.Plan.Items, item => fixture.Blobs.Verify(item.Blob));
        Assert.All(new FileEffectOutbox(fixture.Database).Read(fixture.Plan.JournalId), effect => Assert.Equal(FileEffectState.NotExecuted, effect.State));
    }

    [Fact(DisplayName = "A New Reference After Applying Starts Cancels The Started Removal And Preserves The Blob")]
    public void KeepsNewReferences()
    {
        var fixture = Create();
        Accept(fixture);
        var observer = new ApplyReference(fixture);
        Assert.Equal(FileCleanupResult.Cleaned, new FileCleanupRecovery(Open(fixture, observer)).Execute());
        Assert.True(observer.Referenced);
        fixture.Blobs.Verify(fixture.Plan.Items[0].Blob);
        Assert.Equal(FileEffectState.NotExecuted, Assert.Single(new FileEffectOutbox(fixture.Database).Read(fixture.Plan.JournalId)).State);
    }

    [Fact(DisplayName = "An Unknown Later Cleanup Object Prevents Every Earlier Deletion And Keeps Isolation")]
    public void RejectsUnknownObjects()
    {
        var fixture = Create(2);
        Accept(fixture);
        var changed = Path.Combine(fixture.Instance.RuntimeRoot, fixture.Plan.Items[1].Blob.RelativePath);
        File.WriteAllText(changed, "preserve unknown bytes");
        Assert.Equal(FileCleanupResult.Quarantined, new FileCleanupRecovery(Open(fixture)).Execute());
        fixture.Blobs.Verify(fixture.Plan.Items[0].Blob);
        Assert.Equal("preserve unknown bytes", File.ReadAllText(changed));
        Assert.Equal(2, new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Isolations.Count);
    }

    [Fact(DisplayName = "A Started Outbox With An Unbound Completion Receipt Cannot Authorize Recovery Or Another Deletion")]
    public void RejectsUnboundReceipts()
    {
        var fixture = Create();
        Accept(fixture);
        Assert.Throws<ControlledInterruption>(() => new FileCleanupRecovery(Open(fixture, new InterruptAt(CleanupPersistencePoint.SqlProjectionCommitted, 3))).Execute());
        new FileEffectOutbox(fixture.Database).Record(fixture.Plan.JournalId, fixture.Plan.Items[0].StepId,
            FileEffectState.Started, FileEffectState.Completed, new string('f', 64));
        var before = new RecoveryJournalRegistry(fixture.Database).ReadSnapshot();
        Assert.Throws<IOException>(() => Open(fixture));
        fixture.Blobs.Verify(fixture.Plan.Items[0].Blob);
        Assert.Equal(before.Revision, new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Revision);
    }

    private static Fixture Create(int count = 1)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        var database = StateDatabase.CreateFresh(instance);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var candidates = new List<BlobReference>();
        for (var index = 0; index < count; index++)
        {
            var source = Path.Combine(root, $"source-{index}.bin");
            File.WriteAllBytes(source, [0, 255, (byte)index, 12]);
            var entry = Assert.Single(new FileTreeScanner(SnapshotBudget.Default).Capture(source, default).Entries);
            candidates.Add(blobs.PersistContent(source, entry, default));
        }
        var plan = new CleanupPlanCodec(instance, contracts, blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), candidates);
        return new(instance, database, blobs, plan);
    }

    private static void Accept(Fixture fixture, ICleanupPersistenceObserver? observer = null) =>
        new FileCleanupAcceptance(fixture.Instance, new(), fixture.Database, observer).Accept(
            new(fixture.Plan.OperationId, "fixture-subject", new string('a', 64), "file.cleanup"), fixture.Plan, DateTimeOffset.UtcNow);

    private static CleanupRecoverySession Open(Fixture fixture, ICleanupPersistenceObserver? observer = null) =>
        new(fixture.Instance, fixture.Plan.JournalId, new(), fixture.Database,
            () => new(fixture.Instance.InstanceId, fixture.Plan.JournalId, true, WriterState.Stopped), observer);

    private static string[] Fingerprints(string root) => Directory.GetFiles(Path.Combine(root, "journals"), "*", SearchOption.AllDirectories)
        .Concat(Directory.GetFiles(Path.Combine(root, "blobs"), "*", SearchOption.AllDirectories)).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private sealed record Fixture(RuntimeInstance Instance, StateDatabase Database, ImmutableBlobStore Blobs, FrozenFileCleanup Plan);
    private sealed class ControlledInterruption : Exception { }

    private sealed class InterruptAt(CleanupPersistencePoint target, long targetRevision) : ICleanupPersistenceObserver
    {
        public bool ReachedBoundary { get; private set; }
        public void Reached(CleanupPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            if (point != target || revision != targetRevision) return;
            ReachedBoundary = true;
            throw new ControlledInterruption();
        }
    }

    private sealed class ApplyReference(Fixture fixture) : ICleanupPersistenceObserver
    {
        public bool Referenced { get; private set; }
        public void Reached(CleanupPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            if (point != CleanupPersistencePoint.SqlProjectionCommitted || revision != 2) return;
            var record = RecordStoreFixture.Record(1, Guid.NewGuid());
            new RunRecordStore(fixture.Database).AppendCompleted([record]);
            var blob = fixture.Plan.Items[0].Blob;
            new RecordAssetStore(fixture.Database, fixture.Blobs).AttachVerified(new(record.FinalAttempt.RunId, blob.Sha256, blob.SizeBytes, RecordAssetKind.Screenshot));
            Referenced = true;
        }
    }
}
