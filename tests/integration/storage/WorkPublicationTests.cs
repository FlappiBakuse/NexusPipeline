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
        await using var fixture = await WorkCleanupFixture.CreateAsync(files: 2);
        var owner = fixture.Original.Package.Catalog;
        var unknown = Path.Combine(fixture.Workspace, "unknown-after-first.bin"); var bytes = new byte[] { 9, 7, 3 };
        var originals = Directory.GetFiles(fixture.Workspace, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => (Path: path, Identity: PhysicalObjectIdentity.Read(path), Bytes: File.ReadAllBytes(path))).ToArray();
        Assert.True(originals.Length >= 2);
        PhysicalObjectIdentity? identity = null;
        var outbox = new FileEffectOutbox(owner.Database);
        string? firstDeletion = null;
        string? removedPath = null;
        var observer = new AfterFact(CleanupPersistencePoint.SqlProjectionCommitted, JournalPhase.Applying, () =>
        {
            removedPath = Assert.Single(originals, original => !File.Exists(original.Path)).Path;
            File.WriteAllBytes(unknown, bytes); identity = PhysicalObjectIdentity.Read(unknown);
        }, journalId =>
        {
            firstDeletion = outbox.Read(journalId).SingleOrDefault(item => item.State == FileEffectState.Completed)?.StepId;
            return firstDeletion is not null;
        });
        var result = await fixture.Service().RunPageAsync(Guid.NewGuid(), "local", fixture.Producer.Plan.JournalId,
            fixture.Producer.PlanReference, null, null, default, observer);
        Assert.True(observer.Fired); Assert.Equal(JournalPhase.Quarantined,
            new RecoveryJournalRegistry(owner.Database).ReadJournal(result.JournalId).Phase);
        Assert.Equal(firstDeletion, Assert.Single(outbox.Read(result.JournalId), item => item.State == FileEffectState.Completed).StepId);
        Assert.NotNull(removedPath);
        Assert.False(File.Exists(removedPath));
        Assert.All(originals.Where(original => original.Path != removedPath), original =>
        {
            Assert.Equal(original.Identity, PhysicalObjectIdentity.Read(original.Path));
            Assert.Equal(original.Bytes, File.ReadAllBytes(original.Path));
        });
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
        var observer = new AfterFact(CleanupPersistencePoint.SqlProjectionCommitted, JournalPhase.Committed,
            () => File.WriteAllBytes(path, bytes));
        _ = await fixture.Service().RunPageAsync(Guid.NewGuid(), "local", fixture.Producer.Plan.JournalId,
            fixture.Producer.PlanReference, null, null, default, observer);
        Assert.True(observer.Fired); Assert.False(owner.Index.Current.IsReady);
        var admission = owner.Resources.TryAcquire(Guid.NewGuid(), [new("extension:unrelated", ResourceClaimMode.Mutation)]);
        Assert.Null(admission.Lease); Assert.Equal(ResourceLeaseDenial.HostUnavailable, admission.Denial);
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.False(Directory.Exists(fixture.Workspace));
    }

    private sealed class AfterFact(CleanupPersistencePoint expected, JournalPhase phase, Action action, Func<Guid, bool>? ready = null)
        : ICleanupPersistenceObserver
    {
        internal bool Fired;
        public void Reached(CleanupPersistencePoint point, Guid journalId, long actualRevision, JournalPhase actualPhase)
        {
            if (Fired || point != expected || phase != actualPhase || ready?.Invoke(journalId) == false) return;
            Fired = true; action();
        }
    }
}
