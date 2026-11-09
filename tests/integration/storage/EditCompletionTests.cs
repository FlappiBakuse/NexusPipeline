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
        AssertSessionHolds(index, edit.Session);
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, resources.TryAcquire(Guid.NewGuid(),
            [new(ConfigurationRevisionService.ResourceKey(edit.Session.Resources[0].Address), ResourceClaimMode.Mutation)]).Denial);
        var closing = Guid.NewGuid(); Assert.Equal("recovery_pending", lifecycle.RequestClose(closing, Guid.NewGuid()).ReasonCode);
        if (save)
        {
            var completion = edit.Completion(); var operation = ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion);
            var saved = service.Save(operation, completion, DateTimeOffset.UtcNow);
            Assert.True(saved.IsNew);
            Assert.NotNull(lifecycle.RequestClose(closing, Guid.NewGuid()).Permit);
            var replay = service.Save(operation, completion, DateTimeOffset.UtcNow);
            Assert.False(replay.IsNew);
            AssertSavedBatch(edit, operation.OperationId, completion.Batch, replay);
        }
        else
        {
            var cancel = new ConfigurationEditCancel(edit.SessionId, 2); var operation = ConfigurationEditStore.CancelIdentity(Guid.NewGuid(), "fixture", cancel);
            var cancelled = service.Cancel(operation, cancel, DateTimeOffset.UtcNow);
            Assert.True(cancelled.IsNew);
            Assert.Equal(ConfigurationEditPhase.Cancelled, cancelled.Phase);
            Assert.Equal(edit.SessionId, cancelled.SessionId); Assert.Equal(operation.OperationId, cancelled.CompletionOperationId);
            Assert.NotNull(lifecycle.RequestClose(closing, Guid.NewGuid()).Permit);
            var replay = service.Cancel(operation, cancel, DateTimeOffset.UtcNow);
            Assert.False(replay.IsNew);
            Assert.Equal(cancelled.SessionId, replay.SessionId); Assert.Equal(cancelled.Phase, replay.Phase);
            Assert.Equal(cancelled.Revision, replay.Revision); Assert.Equal(cancelled.CompletionOperationId, replay.CompletionOperationId);
            Assert.Equal(cancelled.Resources.ToArray(), replay.Resources.ToArray());
            AssertOriginalSnapshots(edit, edit.Initial.Changes);
        }
        Assert.Empty(index.Current.ResourceHolds); Assert.True(lifecycle.CanFinalizeAfterExit(closing));
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
        if (fault == "conflict") Assert.NotNull(held);
        if (fault == "unknown") index.BlockUnknown("fixture_unknown");
        if (fault == "isolation") index.PublishVerified(index.Current.Revision, index.Current.Revision + 1, [new(key, Guid.NewGuid(), "writer_unknown")]);
        if (fault == "foreign") index.UpdateResourceHolds(RecoveryHoldOwner.StartupRegistration, [new(Guid.NewGuid(), key, "pending")]);
        if (fault == "missingHolds") index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, []);
        var completion = edit.Completion(); var operation = ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion);
        Assert.Equal(fault == "conflict" ? "configuration_resource_conflict" : "configuration_host_unavailable",
            Assert.Throws<ConfigurationException>(() => service.Save(operation, completion, DateTimeOffset.UtcNow)).Message);
        Assert.Null(edit.Store.LookupSave(operation, completion));
        Assert.Equal(ConfigurationEditPhase.Editing, edit.Store.Read(edit.SessionId, "fixture")!.Phase);
        AssertOriginalSnapshots(edit, edit.Initial.Changes); edit.VerifyOriginals();
    }

    [Fact(DisplayName = "Failed Session Publication Retains All Recovery Holds And Can Explicitly Retry The Same Unaccepted Save")]
    public void RetainsFailedPublication()
    {
        var edit = new ConfigurationEditFixture(); edit.InstallAll(); edit.RestoreAll();
        using var lease = InstanceLease.Acquire(edit.Base.Instance, new()); var (index, lifecycle, _, service) = Ready(edit, lease);
        var completion = edit.Completion(); var operation = ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion);
        AssertSessionHolds(index, edit.Session);
        var before = index.Current.ResourceHolds.OrderBy(hold => hold.ResourceKey, StringComparer.Ordinal).ToArray();
        edit.Base.Execute("CREATE TRIGGER fixture_fail_completion BEFORE UPDATE ON configuration_edit_sessions WHEN NEW.phase=2 BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        try { Assert.Throws<SqliteException>(() => service.Save(operation, completion, DateTimeOffset.UtcNow)); }
        finally { edit.Base.Execute("DROP TRIGGER fixture_fail_completion;"); }
        Assert.Null(edit.Store.LookupSave(operation, completion));
        Assert.Equal(before, index.Current.ResourceHolds.OrderBy(hold => hold.ResourceKey, StringComparer.Ordinal));
        AssertOriginalSnapshots(edit, edit.Initial.Changes);
        var closing = Guid.NewGuid(); Assert.Equal("recovery_pending", lifecycle.RequestClose(closing, Guid.NewGuid()).ReasonCode);
        var saved = service.Save(operation, completion, DateTimeOffset.UtcNow);
        Assert.True(saved.IsNew);
        AssertSavedBatch(edit, operation.OperationId, completion.Batch, saved);
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
        Assert.All(index.Current.ResourceHolds, hold => Assert.Equal(secondId, hold.OperationId));
        AssertSessionHolds(index, second);
        Assert.Equal("recovery_pending", lifecycle.RequestClose(closing, Guid.NewGuid()).ReasonCode);
        var cancel = new ConfigurationEditCancel(secondId, 2); service.Cancel(ConfigurationEditStore.CancelIdentity(Guid.NewGuid(), "fixture", cancel), cancel, DateTimeOffset.UtcNow);
        Assert.Empty(index.Current.ResourceHolds); Assert.NotNull(lifecycle.RequestClose(closing, Guid.NewGuid()).Permit);
        AssertOriginalSnapshots(first, initial);
        first.VerifyOriginals();
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
        var operation = ConfigurationEditStore.SaveIdentity(Guid.NewGuid(), "fixture", completion);
        var saved = service.Save(operation, completion, DateTimeOffset.UtcNow);
        Assert.True(saved.IsNew);
        AssertSavedBatch(edit, operation.OperationId, completion.Batch, saved);
        var blobs = new ImmutableBlobStore(edit.Base.Instance, new());
        foreach (var item in saved.Items)
        {
            var entry = Assert.Single(item.Saved.Tree.Entries);
            using var stream = blobs.OpenVerified(new($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!));
            using var reader = new StreamReader(stream);
            Assert.Equal("continued-" + item.Saved.Address.ResourceId, reader.ReadToEnd());
        }
        Assert.Empty(index.Current.ResourceHolds); edit.VerifyOriginals(); ConfigurationEditInventory.VerifyReadOnly(reopened);
    }

    private static void AssertSessionHolds(RecoveryIndex index, ConfigurationEditStatus session)
    {
        var holds = index.Current.ResourceHolds;
        foreach (var resource in session.Resources)
            Assert.Contains(holds, hold => hold.OperationId == session.SessionId && hold.Owner == RecoveryHoldOwner.ConfigurationOperations &&
                hold.ResourceKey == ConfigurationRevisionService.ResourceKey(resource.Address) && hold.Mode == ResourceClaimMode.Mutation);
    }

    private static void AssertSavedBatch(ConfigurationEditFixture edit, Guid operationId, ConfigurationRevisionBatch batch, ConfigurationBatchResult result)
    {
        Assert.Equal(operationId, result.OperationId);
        Assert.Equal(batch.Changes.Length, result.Items.Length);
        foreach (var change in batch.Changes)
        {
            var item = Assert.Single(result.Items, item => item.Saved.Address == change.Address);
            var current = edit.Base.Revisions.Read(change.Address)!;
            Assert.Equal(change.ExpectedRevision + 1, item.Saved.Revision);
            Assert.Equal(item.Saved.Revision, current.Revision);
            Assert.Equal(change.Tree.Shape, current.Tree.Shape); Assert.Equal(change.Tree.Entries, current.Tree.Entries);
            Assert.Equal(change.Tree.Shape, item.Saved.Tree.Shape); Assert.Equal(change.Tree.Entries, item.Saved.Tree.Entries);
        }
    }

    private static void AssertOriginalSnapshots(ConfigurationEditFixture edit, IEnumerable<ConfigurationRevisionChange> changes)
    {
        foreach (var change in changes)
        {
            var current = edit.Base.Revisions.Read(change.Address)!;
            Assert.Equal(1, current.Revision); Assert.Equal(change.Tree.Entries, current.Tree.Entries);
        }
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
