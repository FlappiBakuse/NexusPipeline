using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Platform.Storage.Operations;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class MaintenancePendingTests
{
    [Theory(DisplayName = "Maintenance Metadata Queries And Generic Operation Queries Respect Subjects Without An Initial Journal")]
    [InlineData(false)]
    [InlineData(true)]
    public void BindsMaintenanceSubjects(bool cancel)
    {
        using var fixture = PayloadTestFixture.Create(JournalKind.UpdateApply, PayloadAction.Upgrade);
        var row = Seed(fixture);
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var store = Store(fixture, lease);
        if (cancel) Assert.Equal(HostUpdateState.Cancelled, store.Cancel(row.Identity.OperationId, row.Identity.SubjectId)!.State);
        var operations = new OperationQueryService(fixture.Instance, lease, fixture.Database);
        var generic = operations.Read(row.Identity.OperationId, row.Identity.SubjectId)!;
        Assert.Null(generic.InitialJournalId);
        Assert.Equal(cancel ? "completed" : "accepted", generic.Status);
        Assert.Null(operations.Read(row.Identity.OperationId, "another-subject"));
        Assert.Null(store.Read(row.Identity.OperationId, "another-subject"));
        Assert.Equal(cancel ? HostUpdateState.Cancelled : HostUpdateState.Pending, store.Read(row.Identity.OperationId, row.Identity.SubjectId)!.State);
        Assert.Empty(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
        MaintenancePendingInventory.VerifyReadOnly(fixture.Instance, fixture.Database, new(), fixture.Roots);
    }

    [Fact(DisplayName = "Cancelling An Unhanded Pending Update Is Idempotent And Creates No Journal Effect Or File Change")]
    public void CancelsUnhandedUpdates()
    {
        using var fixture = PayloadTestFixture.Create(JournalKind.UpdateApply, PayloadAction.Upgrade);
        var row = Seed(fixture);
        using var lease = InstanceLease.Acquire(fixture.Instance, new()); var store = Store(fixture, lease);
        var first = store.Cancel(row.Identity.OperationId, row.Identity.SubjectId);
        Assert.Equal(first, store.Cancel(row.Identity.OperationId, row.Identity.SubjectId));
        var registry = new RecoveryJournalRegistry(fixture.Database).ReadSnapshot();
        Assert.Empty(registry.Journals); Assert.Empty(registry.Isolations); Assert.Empty(registry.FileEffects);
        Assert.Equal(fixture.Plan.Before.Snapshot.Entries, fixture.Scanner.Capture(fixture.Plan.TargetPath, default).Entries);
        Assert.Equal(fixture.Plan.Before.OriginalObjects, PhysicalTreeIdentity.Capture(fixture.Plan.TargetPath,
            fixture.Scanner.Capture(fixture.Plan.TargetPath, default)));
    }

    [Fact(DisplayName = "A Registered Starting Update Cannot Be Cancelled Or Rewritten As An Unattempted Pending Update")]
    public void RejectsStartedCancellation()
    {
        using var fixture = PayloadTestFixture.Create(JournalKind.UpdateApply, PayloadAction.Upgrade);
        var row = Seed(fixture); var handoff = Guid.NewGuid();
        fixture.Database.Write((connection, transaction) =>
        {
            MaintenanceOperationRows.MarkStarting(connection, transaction, row, handoff);
            var plan = fixture.Plan;
            RecoveryJournalRegistry.RegisterInside(connection, transaction,
                new(plan.JournalId, plan.OperationId, JournalKind.UpdateApply, JournalPhase.Prepared, 1, row.Plan.RelativePath, row.Plan.Sha256, plan.ResourceKeys),
                PayloadFileIntents.Create(plan, row.Plan.Sha256), JournalWriterStore.CurrentWriter);
            return true;
        });
        using var lease = InstanceLease.Acquire(fixture.Instance, new()); var store = Store(fixture, lease);
        Assert.Throws<IOException>(() => store.Cancel(row.Identity.OperationId, row.Identity.SubjectId));
        var actual = store.Read(row.Identity.OperationId, row.Identity.SubjectId)!;
        Assert.Equal(HostUpdateState.Starting, actual.State); Assert.Equal(fixture.Plan.JournalId, actual.JournalId);
        using var connection = DatabaseConnections.Open(fixture.Database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Equal(handoff, MaintenanceOperationRows.ReadOwned(connection, transaction, row.Identity.OperationId, row.Identity.SubjectId)!.HandoffId);
        transaction.Commit();
    }

    [Fact(DisplayName = "A Failed Pending Metadata Insert Rolls Back Its Accepted Operation And Does Not Create An Effect Journal")]
    public void RevertsPendingFailure()
    {
        using var fixture = PayloadTestFixture.Create(JournalKind.UpdateApply, PayloadAction.Upgrade);
        using var frozen = fixture.Codec.Freeze(fixture.Plan);
        var source = new UpdateSourceCodec(fixture.Instance, new()).ReadMetadata(fixture.Plan.MaintenanceSource!);
        var identity = UpdateRequestIdentity.FromSource(source, "local");
        Assert.Throws<IOException>(() => fixture.Database.Write<bool>((connection, transaction) =>
        {
            OperationRows.Accept(connection, transaction, identity, DateTimeOffset.UtcNow);
            MaintenanceOperationRows.InsertPending(connection, transaction, identity, frozen.Reference, fixture.Plan.MaintenanceSource!);
            throw new IOException("controlled-pending-failure");
        }));
        using var connection = DatabaseConnections.Open(fixture.Database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Null(OperationRows.ReadOwned(connection, transaction, identity.OperationId, identity.SubjectId));
        Assert.Empty(MaintenanceOperationRows.ReadAll(connection, transaction)); transaction.Commit();
        Assert.Empty(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
    }

    [Fact(DisplayName = "Pending Startup Inventory Rejects An Accepted Maintenance Operation Without Its Only Owner Metadata")]
    public void RequiresOwnerMetadata()
    {
        using var fixture = PayloadTestFixture.Create(JournalKind.UpdateApply, PayloadAction.Upgrade);
        var source = new UpdateSourceCodec(fixture.Instance, new()).ReadMetadata(fixture.Plan.MaintenanceSource!);
        fixture.Database.Accept(UpdateRequestIdentity.FromSource(source, "local"), DateTimeOffset.UtcNow);
        Assert.Throws<IOException>(() => MaintenancePendingInventory.VerifyReadOnly(fixture.Instance, fixture.Database, new(), fixture.Roots));
    }

    [Theory(DisplayName = "Actual Prepared Recovery Projects Maintenance Terminal State Together With Its Original Journal")]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectsTerminalRecovery(bool unknownWriter)
    {
        using var fixture = PayloadTestFixture.Create(JournalKind.UpdateApply, PayloadAction.Upgrade);
        var row = Seed(fixture);
        fixture.Database.Write((connection, transaction) =>
        {
            MaintenanceOperationRows.MarkStarting(connection, transaction, row, Guid.NewGuid());
            var plan = fixture.Plan;
            RecoveryJournalRegistry.RegisterInside(connection, transaction,
                new(plan.JournalId, plan.OperationId, JournalKind.UpdateApply, JournalPhase.Prepared, 1, row.Plan.RelativePath, row.Plan.Sha256, plan.ResourceKeys),
                PayloadFileIntents.Create(plan, row.Plan.Sha256), JournalWriterStore.CurrentWriter);
            return true;
        });
        var result = unknownWriter ? fixture.Recover(id => new(fixture.Instance.InstanceId, id, false, NexusPipeline.Core.Execution.WriterState.Unknown)) : fixture.Recover();
        Assert.Equal(unknownWriter ? JournalRecoveryResult.Quarantined : JournalRecoveryResult.NotExecuted, result);
        using var lease = InstanceLease.Acquire(fixture.Instance, new()); var store = Store(fixture, lease);
        Assert.Equal(unknownWriter ? HostUpdateState.Quarantined : HostUpdateState.Completed, store.Read(row.Identity.OperationId, row.Identity.SubjectId)!.State);
        Assert.Equal<HostUpdateOutcome?>(unknownWriter ? null : HostUpdateOutcome.NotExecuted, store.Read(row.Identity.OperationId, row.Identity.SubjectId)!.Outcome);
        Assert.Equal(unknownWriter ? "accepted" : "completed", new OperationQueryService(fixture.Instance, lease, fixture.Database)
            .Read(row.Identity.OperationId, row.Identity.SubjectId)!.Status);
        MaintenancePendingInventory.VerifyReadOnly(fixture.Instance, fixture.Database, new(), fixture.Roots);
        Assert.Equal(fixture.Plan.Before.Snapshot.Entries, fixture.Scanner.Capture(fixture.Plan.TargetPath, default).Entries);
    }

    [Fact(DisplayName = "Actual Content Cleanup Retains An Unhanded Pending Archive And Both Trees Without An Effect Journal")]
    public void ProtectsPendingContent()
    {
        using var fixture = PayloadTestFixture.Create(JournalKind.UpdateApply, PayloadAction.Upgrade);
        _ = Seed(fixture);
        var blobs = new ImmutableBlobStore(fixture.Instance, new());
        var source = new UpdateSourceCodec(fixture.Instance, new()).ReadMetadata(fixture.Plan.MaintenanceSource!);
        var index = new BlobReferenceIndex(fixture.Instance, new(), fixture.Database, blobs, fixture.Roots);
        foreach (var content in fixture.Plan.Before.Contents.Concat(fixture.Plan.Candidate.Contents).Append(source.Archive))
            Assert.True(index.IsReferenced(content));
        var cleanup = new CleanupPlanCodec(fixture.Instance, new(), blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), [source.Archive]);
        var item = Assert.Single(cleanup.Items);
        var gc = new BlobCleanup(fixture.Instance, blobs, index);
        Assert.Equal(CleanupItemState.Referenced, gc.Inspect(item));
        Assert.Equal(CleanupStepResult.SkippedReferenced, gc.RemoveVerified(item));
        Assert.Throws<IOException>(() => new BlobReferenceIndex(fixture.Instance, new(), fixture.Database, blobs).IsReferenced(source.Archive));
        Assert.True(File.Exists(Path.Combine(fixture.Instance.RuntimeRoot, source.Archive.RelativePath)));
        Assert.Empty(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
    }

    private static MaintenancePendingStore Store(PayloadTestFixture fixture, InstanceLease lease)
    {
        var recovery = new RecoveryIndex(); recovery.PublishVerified(0, 1, []);
        var resources = new ResourceLeaseManager(new(recovery, TimeProvider.System));
        return new(fixture.Instance, fixture.Database, lease, resources, new(), fixture.Roots, UpdateSourceSeed.Verifier(new()));
    }

    private static MaintenanceOperationRow Seed(PayloadTestFixture fixture)
    {
        using var frozen = fixture.Codec.Freeze(fixture.Plan);
        var source = new UpdateSourceCodec(fixture.Instance, new()).ReadMetadata(fixture.Plan.MaintenanceSource!);
        var identity = UpdateRequestIdentity.FromSource(source, "local");
        return fixture.Database.Write((connection, transaction) =>
        {
            OperationRows.Accept(connection, transaction, identity, DateTimeOffset.UtcNow);
            MaintenanceOperationRows.InsertPending(connection, transaction, identity, frozen.Reference, fixture.Plan.MaintenanceSource!);
            return MaintenanceOperationRows.ReadOwned(connection, transaction, identity.OperationId, identity.SubjectId)!;
        });
    }
}
