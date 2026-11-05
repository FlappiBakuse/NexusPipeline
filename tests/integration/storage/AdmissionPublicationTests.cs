using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class AdmissionPublicationTests
{
    [Fact(DisplayName = "A Registered Manual Edit Without An Installed Journal Does Not Block An Independent Execution Or Lose Its Isolation")]
    public void PreservesIndependentIsolation()
    {
        using var fixture = new AdmissionPublicationFixture(); var admission = fixture.Admission; var source = admission.Source;
        var configuration = File.ReadAllBytes(source.Configuration); var log = File.ReadAllBytes(source.Log);
        var identity = PhysicalObjectIdentity.Read(source.Configuration); var operation = Guid.NewGuid();
        var accepted = admission.Service.Start(operation, "execution-publication", source.Request(new BindingTarget(source.OtherBinding)), PlanBuilderFixture.CreatedAt);
        Assert.True(accepted.Acceptance.IsNew); Assert.Equal(1, admission.Inbox.PendingCount); Assert.True(admission.Index.Current.IsReady);
        var snapshot = new RecoveryJournalRegistry(source.Database).ReadSnapshot();
        var edit = Assert.Single(snapshot.Journals, item => item.JournalId == fixture.Edit.Resources[0].JournalId);
        Assert.Equal(JournalPhase.Prepared, edit.Phase); Assert.Equal(1, edit.Revision); Assert.False(Directory.Exists(fixture.JournalDirectory));
        Assert.Contains(admission.Index.Current.Isolations, item => item.JournalId == edit.JournalId &&
            item.ResourceKey == "automation:definitions" && item.Mode == ResourceClaimMode.Reference);
        var slot = ConfigurationRevisionService.ResourceKey(fixture.Request.Targets[0].Address);
        Assert.Contains(admission.Index.Current.Isolations, item => item.JournalId == edit.JournalId && item.ResourceKey == slot && item.Mode == ResourceClaimMode.Mutation);
        Assert.Null(admission.Resources.TryAcquire(Guid.NewGuid(), [new(slot, ResourceClaimMode.Mutation)]).Lease);
        Assert.Empty(admission.Index.Current.SnapshotCompletions); Assert.Equal(0, admission.Count("attempt_receipts"));
        Assert.Equal(configuration, File.ReadAllBytes(source.Configuration)); Assert.Equal(log, File.ReadAllBytes(source.Log));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(source.Configuration));
        var startup = new StartupJournalRecovery(source.Instance, new(), source.Database, fixture.Roots);
        startup.InspectBindings(snapshot);
        Assert.Throws<IOException>(() => startup.VerifyReadOnly(snapshot));
        Assert.Throws<IOException>(() => new JournalRecoveryEvidence(source.Instance, new(source.Instance, new())).VerifyAll(snapshot.Journals));
        Assert.Equal(ConfigurationEditPhase.Prepared, fixture.Store.Read(fixture.Edit.SessionId, "admission-edit")!.Phase);
    }

    [Theory(DisplayName = "An Unrecognized Or Incorrect Initial Edit Cannot Be Published Or Repaired By Admission Evidence")]
    [InlineData("missing-plan")]
    [InlineData("unknown-plan")]
    [InlineData("foreign-plan")]
    [InlineData("claim-mode")]
    public void RejectsInvalidEdits(string fault)
    {
        using var fixture = new AdmissionPublicationFixture(); var admission = fixture.Admission; var source = admission.Source;
        fixture.DamageInitialPlan(fault); var before = source.Snapshot();
        var registry = new RecoveryJournalRegistry(source.Database);
        Assert.ThrowsAny<IOException>(() => new RecoveryCoordinator(registry, fixture.Evidence(), admission.Index).VerifyAndPublish());
        var after = source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        Assert.False(admission.Index.Current.IsReady); Assert.Empty(admission.Index.Current.SnapshotCompletions);
        Assert.False(Directory.Exists(fixture.JournalDirectory)); Assert.Equal(0, admission.Inbox.PendingCount);
    }

    [Fact(DisplayName = "Publication Cannot Verify Only A Subset Of The Registered Inventory")]
    public void RequiresWholeInventory()
    {
        using var fixture = new AdmissionPublicationFixture(); var before = fixture.Admission.Source.Snapshot();
        Assert.Throws<IOException>(() => fixture.Evidence().VerifyAll([]));
        var after = fixture.Admission.Source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
        Assert.False(Directory.Exists(fixture.JournalDirectory)); Assert.Empty(fixture.Admission.Index.Current.SnapshotCompletions);
    }
}
