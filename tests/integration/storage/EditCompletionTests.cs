using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class EditCompletionTests
{
    [Theory(DisplayName = "Reopened Restored Edit Finishes Its Own Snapshots During Exit Draining And Replays Without New Effects")]
    [InlineData("missing", false)]
    [InlineData("file", false)]
    [InlineData("directory", false)]
    [InlineData("missing", true)]
    [InlineData("file", true)]
    [InlineData("directory", true)]
    public void CompletesDuringDrain(string shape, bool save)
    {
        var edit = new ConfigurationEditFixture(shape); edit.InstallAll(); edit.RestoreAll();
        using var lease = InstanceLease.Acquire(edit.Base.Instance, new());
        var reopened = StateDatabase.OpenExisting(edit.Base.Instance, Path.Combine(edit.Base.Root, "reopen"), new JournalStartupAdmission(edit.Base.Instance, new()));
        var (index, lifecycle, resources, service) = Ready(edit, lease, reopened);
        Assert.All(index.Current.ResourceHolds, hold => Assert.Equal(RecoveryHoldOwner.ConfigurationOperations, hold.Owner));
        Assert.NotEmpty(index.Current.ResourceHolds);
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, resources.TryAcquire(Guid.NewGuid(),
            [new(ConfigurationRevisionService.ResourceKey(edit.Session.Resources[0].Address), ResourceClaimMode.Mutation)]).Denial);
        var closing = Guid.NewGuid(); Assert.Equal("recovery_pending", lifecycle.RequestClose(closing, Guid.NewGuid()).ReasonCode);
        if (save)
        {
            var completion = edit.Completion(); var operation = ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion);
            Assert.True(service.Save(operation, completion, DateTimeOffset.UtcNow).IsNew);
            Assert.NotNull(lifecycle.RequestClose(closing, Guid.NewGuid()).Permit);
            Assert.False(service.Save(operation, completion, DateTimeOffset.UtcNow).IsNew);
        }
        else
        {
            var cancel = new ConfigurationEditCancel(edit.SessionId, 2); var operation = ConfigurationEditStore.CancelIdentity(Guid.NewGuid(), "fixture", cancel);
            Assert.True(service.Cancel(operation, cancel, DateTimeOffset.UtcNow).IsNew);
            Assert.NotNull(lifecycle.RequestClose(closing, Guid.NewGuid()).Permit);
            Assert.False(service.Cancel(operation, cancel, DateTimeOffset.UtcNow).IsNew);
        }
        Assert.Empty(index.Current.ResourceHolds); Assert.True(lifecycle.CanFinalizeAfterExit(closing));
        Assert.All(edit.Initial.Changes, change => Assert.Equal(save ? 2 : 1, edit.Base.Revisions.Read(change.Address)!.Revision));
        edit.VerifyOriginals(); ConfigurationEditInventory.VerifyReadOnly(reopened);
    }

    [Fact(DisplayName = "Completion Cannot Acquire A Continuation Until Every Original Scene Has Been Restored")]
    public void RequiresRestoredScenes()
    {
        var edit = new ConfigurationEditFixture(); edit.InstallAll();
        using var lease = InstanceLease.Acquire(edit.Base.Instance, new());
        var index = new RecoveryIndex(); index.PublishVerified(0, 1, []); ConfigurationEditRecovery.PublishHolds(edit.Base.Database, index);
        var resources = new ResourceLeaseManager(new(index, TimeProvider.System));
        var service = new EditCompletionService(edit.Base.Instance, edit.Base.Database, new(), lease, resources, index);
        var completion = edit.Completion(); var operation = ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion);
        Assert.Equal("configuration_edit_restoration_required", Assert.Throws<ConfigurationException>(() => service.Save(operation, completion, DateTimeOffset.UtcNow)).Message);
        edit.Exchange.Restore(edit.Session.Resources[0].JournalId);
        Assert.Equal("configuration_edit_restoration_required", Assert.Throws<ConfigurationException>(() => service.Save(operation, completion, DateTimeOffset.UtcNow)).Message);
        Assert.Null(edit.Store.LookupSave(operation, completion));
        edit.RestoreAll(); Assert.True(service.Save(operation, completion, DateTimeOffset.UtcNow).IsNew); edit.VerifyOriginals();
    }

    [Theory(DisplayName = "Completion Cannot Use Its Durable Session To Bypass Unknown Isolation Foreign Holds Or Live Lease Conflicts")]
    [InlineData("unknown")]
    [InlineData("isolation")]
    [InlineData("foreign")]
    [InlineData("missingHolds")]
    [InlineData("conflict")]
    public void PreservesRecoveryBarriers(string fault)
    {
        var edit = new ConfigurationEditFixture(); edit.InstallAll(); edit.RestoreAll();
        using var lease = InstanceLease.Acquire(edit.Base.Instance, new());
        var (index, _, resources, service) = Ready(edit, lease);
        var key = ConfigurationRevisionService.ResourceKey(edit.Session.Resources[0].Address);
        var proof = index.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, edit.SessionId, new HashSet<string> { key })!;
        using var held = fault == "conflict" ? resources.TryContinue(proof, [new(key, ResourceClaimMode.Mutation)]).Lease : null;
        if (fault == "unknown") index.BlockUnknown("fixture_unknown");
        if (fault == "isolation") index.PublishVerified(index.Current.Revision, index.Current.Revision + 1, [new(key, Guid.NewGuid(), "writer_unknown")]);
        if (fault == "foreign") index.UpdateResourceHolds(RecoveryHoldOwner.StartupRegistration, [new(Guid.NewGuid(), key, "pending")]);
        if (fault == "missingHolds") index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, []);
        var completion = edit.Completion(); var operation = ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion);
        Assert.Equal(fault == "conflict" ? "configuration_resource_conflict" : "configuration_host_unavailable",
            Assert.Throws<ConfigurationException>(() => service.Save(operation, completion, DateTimeOffset.UtcNow)).Message);
        Assert.Null(edit.Store.LookupSave(operation, completion));
        Assert.Equal(ConfigurationEditPhase.Editing, edit.Store.Read(edit.SessionId, "fixture")!.Phase);
        Assert.All(edit.Initial.Changes, change => Assert.Equal(1, edit.Base.Revisions.Read(change.Address)!.Revision)); edit.VerifyOriginals();
    }

    [Fact(DisplayName = "Failed Session Publication Retains All Recovery Holds And Can Explicitly Retry The Same Unaccepted Save")]
    public void RetainsFailedPublication()
    {
        var edit = new ConfigurationEditFixture(); edit.InstallAll(); edit.RestoreAll();
        using var lease = InstanceLease.Acquire(edit.Base.Instance, new()); var (index, lifecycle, _, service) = Ready(edit, lease);
        var completion = edit.Completion(); var operation = ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion);
        edit.Base.Execute("CREATE TRIGGER fixture_fail_completion BEFORE UPDATE ON configuration_edit_sessions WHEN NEW.phase=2 BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        Assert.Throws<SqliteException>(() => service.Save(operation, completion, DateTimeOffset.UtcNow));
        Assert.Null(edit.Store.LookupSave(operation, completion)); Assert.NotEmpty(index.Current.ResourceHolds);
        Assert.All(edit.Initial.Changes, change => Assert.Equal(1, edit.Base.Revisions.Read(change.Address)!.Revision));
        var closing = Guid.NewGuid(); Assert.Equal("recovery_pending", lifecycle.RequestClose(closing, Guid.NewGuid()).ReasonCode);
        edit.Base.Execute("DROP TRIGGER fixture_fail_completion;");
        Assert.True(service.Save(operation, completion, DateTimeOffset.UtcNow).IsNew);
        Assert.Empty(index.Current.ResourceHolds); Assert.NotNull(lifecycle.RequestClose(closing, Guid.NewGuid()).Permit); edit.VerifyOriginals();
    }

    [Fact(DisplayName = "Completion Rechecks Its Subject And Live Instance Lease Before It Can Use Any Accepted Session")]
    public void RechecksCompletionAuthority()
    {
        var edit = new ConfigurationEditFixture(); edit.InstallAll(); edit.RestoreAll();
        using var lease = InstanceLease.Acquire(edit.Base.Instance, new()); var (index, _, _, service) = Ready(edit, lease);
        var completion = edit.Completion();
        Assert.Throws<OperationConflictException>(() => service.Save(ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "foreign", completion), completion, DateTimeOffset.UtcNow));
        lease.Dispose();
        Assert.Equal("runtime_exclusive_lease_required", Assert.Throws<IOException>(() =>
            service.Save(ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion), completion, DateTimeOffset.UtcNow)).Message);
        Assert.NotEmpty(index.Current.ResourceHolds); Assert.Equal(ConfigurationEditPhase.Editing, edit.Store.Read(edit.SessionId, "fixture")!.Phase);
    }

    [Fact(DisplayName = "Two Restored Accounts Of The Same Script Can Complete Their Own Slots Without Releasing Each Others Holds")]
    public void SeparatesAccountHolds()
    {
        var first = new ConfigurationEditFixture(); first.InstallAll(); first.RestoreAll();
        var initial = first.Initial.Changes.Select(change => change with { Address = new(first.Base.OtherBinding, change.Address.ResourceId) }).ToImmutableArray();
        first.Base.Save(new(initial)); var secondId = Guid.NewGuid();
        var request = new ConfigurationEditRequest(1, initial.Select(change => new ConfigurationTarget(change.Address, 1, change.Contract,
            Path.Combine(first.Base.Root, Guid.NewGuid().ToString("N")))).ToImmutableArray());
        using (var prepared = first.Store.Prepare(secondId, request))
            first.Store.Begin(ConfigurationEditStore.BeginIdentity(secondId, "fixture", request), prepared, DateTimeOffset.UtcNow);
        var second = first.Store.Read(secondId, "fixture")!;
        foreach (var resource in second.Resources) first.Exchange.Install(resource.JournalId);
        second = first.Store.MarkEditing(secondId, "fixture", 1);
        foreach (var resource in second.Resources) Assert.Equal(ConfigurationRecoveryResult.Restored, first.Exchange.Restore(resource.JournalId));
        using var lease = InstanceLease.Acquire(first.Base.Instance, new()); var (index, lifecycle, _, service) = Ready(first, lease);
        var closing = Guid.NewGuid(); Assert.Equal("recovery_pending", lifecycle.RequestClose(closing, Guid.NewGuid()).ReasonCode);
        var completion = first.Completion(); service.Save(ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion), completion, DateTimeOffset.UtcNow);
        Assert.All(index.Current.ResourceHolds, hold => Assert.Equal(secondId, hold.OperationId)); Assert.NotEmpty(index.Current.ResourceHolds);
        Assert.Equal("recovery_pending", lifecycle.RequestClose(closing, Guid.NewGuid()).ReasonCode);
        var cancel = new ConfigurationEditCancel(secondId, 2); service.Cancel(ConfigurationEditStore.CancelIdentity(Guid.NewGuid(), "fixture", cancel), cancel, DateTimeOffset.UtcNow);
        Assert.Empty(index.Current.ResourceHolds); Assert.NotNull(lifecycle.RequestClose(closing, Guid.NewGuid()).Permit);
        Assert.All(initial, change => Assert.Equal(1, first.Base.Revisions.Read(change.Address)!.Revision)); first.VerifyOriginals();
    }

    [Fact(DisplayName = "Actual Edited Output Remains The Saved Candidate After Reopening And Completing Through Shared Admission")]
    public async Task SavesEditedOutput()
    {
        using var fixture = new EditorOutputFixture(); var edit = fixture.Edit;
        await fixture.WriteAsync(edit.Request.Targets.Select(target => (object)new { target = target.TargetPath, kind = "file", value = "continued-" + target.Address.ResourceId }).ToArray());
        var stopped = await fixture.Editor.StopAsync(TimeSpan.FromSeconds(5)); fixture.Editor.Dispose();
        var completion = fixture.Seal(stopped); edit.RestoreAll(); edit.VerifyOriginals();
        var reopened = StateDatabase.OpenExisting(edit.Base.Instance, Path.Combine(edit.Base.Root, "reopen"), new JournalStartupAdmission(edit.Base.Instance, new()));
        var (index, _, _, service) = Ready(edit, fixture.Lease, reopened);
        Assert.True(service.Save(ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion), completion, DateTimeOffset.UtcNow).IsNew);
        foreach (var change in completion.Batch.Changes) Assert.Equal(change.Tree.Entries, edit.Base.Revisions.Read(change.Address)!.Tree.Entries);
        Assert.Empty(index.Current.ResourceHolds); edit.VerifyOriginals(); ConfigurationEditInventory.VerifyReadOnly(reopened);
    }

    private static (RecoveryIndex Index, HostLifecycle Lifecycle, ResourceLeaseManager Resources, EditCompletionService Service)
        Ready(ConfigurationEditFixture edit, InstanceLease lease, StateDatabase? database = null)
    {
        database ??= edit.Base.Database;
        var index = new StartupJournalRecovery(edit.Base.Instance, new(), database,
            new(edit.Base.Root, Path.Combine(edit.Base.Instance.RuntimeRoot, "extensions"))).Recover(lease);
        var lifecycle = new HostLifecycle(index, TimeProvider.System); var resources = new ResourceLeaseManager(lifecycle);
        return (index, lifecycle, resources, new(edit.Base.Instance, database, new(), lease, resources, index));
    }
}
