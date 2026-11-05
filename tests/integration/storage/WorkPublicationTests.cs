using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class WorkPublicationTests
{
    [Fact(DisplayName = "An Unknown Work Object After The First Real Deletion Stops The Next Removal Without Reusing An Inspection Snapshot")]
    public async Task RechecksDeletionFreshness()
    {
        await using var fixture = await WorkCleanupFixture.CreateAsync();
        var owner = fixture.Original.Package.Catalog;
        var unknown = Path.Combine(fixture.Workspace, "unknown-after-first.bin"); var bytes = new byte[] { 9, 7, 3 };
        PhysicalObjectIdentity? identity = null;
        var observer = new AfterFact(CleanupPersistencePoint.SqlProjectionCommitted, JournalPhase.Applying, 4, () =>
        { File.WriteAllBytes(unknown, bytes); identity = PhysicalObjectIdentity.Read(unknown); });
        var result = await fixture.Service().RunPageAsync(Guid.NewGuid(), "local", fixture.Producer.Plan.JournalId,
            fixture.Producer.PlanReference, null, null, default, observer);
        Assert.True(observer.Fired); Assert.Equal(JournalPhase.Quarantined,
            new RecoveryJournalRegistry(owner.Database).ReadJournal(result.JournalId).Phase);
        Assert.Single(new FileEffectOutbox(owner.Database).Read(result.JournalId), item => item.State == FileEffectState.Completed);
        Assert.Equal(bytes, File.ReadAllBytes(unknown)); Assert.Equal(identity, PhysicalObjectIdentity.Read(unknown));
        Assert.Equal(fixture.Producer.Latest.Export(), fixture.Original.Binding().Read(fixture.Producer.Plan.JournalId).Latest.Export());
    }

    [Fact(DisplayName = "An Invalidated Producer Artifact After Work Commit Cannot Publish A Ready Execution Index")]
    public async Task RejectsInvalidatedProducers()
    {
        await using var fixture = await WorkCleanupFixture.CreateAsync();
        var owner = fixture.Original.Package.Catalog;
        var path = Path.Combine(owner.Instance.RuntimeRoot, fixture.Producer.CommitIntent!.Artifact.RelativePath);
        var bytes = new byte[] { 9, 5, 1 };
        var observer = new AfterFact(CleanupPersistencePoint.SqlProjectionCommitted, JournalPhase.Committed, null,
            () => File.WriteAllBytes(path, bytes));
        _ = await fixture.Service().RunPageAsync(Guid.NewGuid(), "local", fixture.Producer.Plan.JournalId,
            fixture.Producer.PlanReference, null, null, default, observer);
        Assert.True(observer.Fired); Assert.False(owner.Index.Current.IsReady);
        var admission = owner.Resources.TryAcquire(Guid.NewGuid(), [new("extension:unrelated", ResourceClaimMode.Mutation)]);
        Assert.Null(admission.Lease); Assert.Equal(ResourceLeaseDenial.HostUnavailable, admission.Denial);
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.False(Directory.Exists(fixture.Workspace));
    }

    private sealed class AfterFact(CleanupPersistencePoint expected, JournalPhase phase, long? revision, Action action)
        : ICleanupPersistenceObserver
    {
        internal bool Fired;
        public void Reached(CleanupPersistencePoint point, Guid journalId, long actualRevision, JournalPhase actualPhase)
        {
            if (Fired || point != expected || phase != actualPhase || revision is { } value && value != actualRevision) return;
            Fired = true; action();
        }
    }
}
