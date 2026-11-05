using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class CompletionAdmissionTests
{
    [Theory(DisplayName = "A Permit Cannot Authorize A Partial Expanded Or Non Mutation Write")]
    [InlineData("partial")]
    [InlineData("extra")]
    [InlineData("physical")]
    [InlineData("script")]
    [InlineData("settings")]
    [InlineData("reference")]
    [InlineData("mixed")]
    public void RequiresExactMutation(string changed)
    {
        var fixture = new SnapshotCompletionFixture(); fixture.Publish(); var permit = fixture.Permit();
        IReadOnlyList<ResourceClaim> claims = changed switch
        {
            "partial" => [fixture.Claims[0]],
            "extra" => fixture.Claims.Append(new("configuration:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa:extra-slot", ResourceClaimMode.Mutation)).ToArray(),
            "physical" => [new("filesystem:object:fixture", ResourceClaimMode.Mutation), fixture.Claims[1]],
            "script" => [new(SnapshotCompletionFixture.Other, ResourceClaimMode.Mutation), fixture.Claims[1]],
            "settings" => [new("settings:host", ResourceClaimMode.Mutation), fixture.Claims[1]],
            "reference" => fixture.Claims.Select(item => item with { Mode = ResourceClaimMode.Reference }).ToArray(),
            _ => [fixture.Claims[0], fixture.Claims[1] with { Mode = ResourceClaimMode.Reference }]
        };
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, fixture.Manager.TryCompleteSnapshots(permit, claims).Denial);
        using var accepted = fixture.Manager.TryCompleteSnapshots(permit, fixture.Claims).Lease; Assert.NotNull(accepted);
    }

    [Theory(DisplayName = "Invalid Duplicate Or Unknown Claim Modes Use The Existing Claim Validation Owner")]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsUnknownClaims(bool mode)
    {
        var fixture = new SnapshotCompletionFixture(); fixture.Publish(); var permit = fixture.Permit();
        var claims = mode ? new[] { fixture.Claims[0] with { Mode = (ResourceClaimMode)99 }, fixture.Claims[1] }
            : new[] { fixture.Claims[0], fixture.Claims[0] };
        Assert.Throws<ArgumentException>(() => fixture.Manager.TryCompleteSnapshots(permit, claims));
    }

    [Theory(DisplayName = "Every Use Rechecks Complete Mutation Holds And Rejects Other Operations Or Owners")]
    [InlineData("removed")]
    [InlineData("partial")]
    [InlineData("foreign-operation")]
    [InlineData("foreign-owner")]
    [InlineData("additional-owner")]
    [InlineData("reference")]
    public void RechecksOwnedHolds(string change)
    {
        var fixture = new SnapshotCompletionFixture(); fixture.Publish(); var permit = fixture.Permit();
        var holds = fixture.Index.Current.ResourceHolds;
        if (change == "removed") fixture.Index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, []);
        if (change == "partial") fixture.SetOwnHolds(SnapshotCompletionFixture.SlotA);
        if (change == "foreign-operation") fixture.Index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations,
            holds.Select(item => item with { OperationId = Guid.NewGuid() }).ToArray());
        if (change == "foreign-owner") fixture.Index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, []);
        if (change is "foreign-owner" or "additional-owner") fixture.Index.UpdateResourceHolds(RecoveryHoldOwner.StartupRegistration, holds);
        if (change == "reference") fixture.Index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations,
            holds.Select(item => item with { Mode = ResourceClaimMode.Reference }).ToArray());
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, fixture.Manager.TryCompleteSnapshots(permit, fixture.Claims).Denial);
        Assert.Null(fixture.Index.AuthorizeSnapshotCompletion(fixture.Parent.JournalId, fixture.Completion.SessionId, fixture.Completion.CompletionOperationId));
    }

    [Fact(DisplayName = "Another Journal On The Same Slot Cannot Be Waived Even When The Parent And Owned Holds Match")]
    public void RejectsForeignJournals()
    {
        var fixture = new SnapshotCompletionFixture(); fixture.AddForeignIsolation(); fixture.Publish();
        Assert.Single(fixture.Index.Current.SnapshotCompletions);
        Assert.Null(fixture.Index.AuthorizeSnapshotCompletion(fixture.Parent.JournalId, fixture.Completion.SessionId, fixture.Completion.CompletionOperationId));
    }

    [Fact(DisplayName = "A Permit From Another Index Cannot Authorize Identical Published Metadata Or Owned Holds")]
    public void RejectsForeignIndexes()
    {
        var fixture = new SnapshotCompletionFixture(); fixture.Publish(); var permit = fixture.Permit();
        var foreign = new RecoveryIndex();
        new RecoveryCoordinator(fixture, fixture, foreign).VerifyAndPublish();
        foreign.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, fixture.Index.Current.ResourceHolds);
        var manager = new ResourceLeaseManager(new(foreign, TimeProvider.System));
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, manager.TryCompleteSnapshots(permit, fixture.Claims).Denial);
        using var accepted = manager.TryCompleteSnapshots(Assert.IsType<SnapshotCompletionPermit>(foreign.AuthorizeSnapshotCompletion(
            fixture.Parent.JournalId, fixture.Completion.SessionId, fixture.Completion.CompletionOperationId)), fixture.Claims).Lease;
        Assert.NotNull(accepted);
    }

    [Fact(DisplayName = "Snapshot Completion Uses The Existing Memory Conflict Accounting And Can Retry The Same Pending Projection")]
    public void PreservesConflictAccounting()
    {
        var fixture = new SnapshotCompletionFixture(); fixture.Publish(); var permit = fixture.Permit();
        using (var accepted = fixture.Manager.TryCompleteSnapshots(permit, fixture.Claims).Lease)
        {
            Assert.NotNull(accepted);
            Assert.Equal(ResourceLeaseDenial.Conflict, fixture.Manager.TryCompleteSnapshots(permit, fixture.Claims).Denial);
        }
        Assert.Equal(2, fixture.Index.Current.ResourceHolds.Count);
        using var retry = fixture.Manager.TryCompleteSnapshots(permit, fixture.Claims).Lease;
        Assert.NotNull(retry); Assert.Equal(permit.Completion.CompletionOperationId, fixture.Permit().Completion.CompletionOperationId);
        Assert.Equal(1, fixture.PublishCalls);
    }

    [Fact(DisplayName = "Publishing Snapshot Evidence Cannot Bypass An Existing Foreign Mutation Lease")]
    public void RejectsForeignLeases()
    {
        var fixture = new SnapshotCompletionFixture();
        fixture.Index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, []);
        fixture.Index.PublishVerified(fixture.Index.Current.Revision, fixture.Index.Current.Revision + 1, []);
        using var foreign = fixture.Manager.TryAcquire(Guid.NewGuid(), fixture.Claims).Lease; Assert.NotNull(foreign);
        fixture.SetOwnHolds(); fixture.Publish(); var permit = fixture.Permit();
        Assert.Equal(ResourceLeaseDenial.Conflict, fixture.Manager.TryCompleteSnapshots(permit, fixture.Claims).Denial);
        foreign.Dispose();
        using var accepted = fixture.Manager.TryCompleteSnapshots(permit, fixture.Claims).Lease; Assert.NotNull(accepted);
    }

    [Fact(DisplayName = "Unrelated Hold Updates Do Not Revoke An Unchanged Pending Completion")]
    public void KeepsUnchangedPermits()
    {
        var fixture = new SnapshotCompletionFixture(); fixture.Publish(); var permit = fixture.Permit();
        fixture.SetOwnHolds(SnapshotCompletionFixture.SlotA, SnapshotCompletionFixture.SlotB, "unrelated:pending");
        using var accepted = fixture.Manager.TryCompleteSnapshots(permit, fixture.Claims).Lease; Assert.NotNull(accepted);
    }

    [Theory(DisplayName = "Exit Draining Admits Only The Complete Mutation Range Held At Its Original Freeze")]
    [InlineData("accepted")]
    [InlineData("late")]
    [InlineData("expanded")]
    [InlineData("reference")]
    public void FreezesMutationRange(string range)
    {
        var fixture = new SnapshotCompletionFixture(); fixture.Publish();
        if (range == "late") fixture.Index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, []);
        if (range == "expanded") fixture.SetOwnHolds(SnapshotCompletionFixture.SlotA);
        if (range == "reference") fixture.Index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations,
            fixture.Index.Current.ResourceHolds.Select(item => item with { Mode = ResourceClaimMode.Reference }).ToArray());
        Assert.Equal("recovery_pending", fixture.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).ReasonCode);
        fixture.SetOwnHolds();
        var admission = fixture.Manager.TryCompleteSnapshots(fixture.Permit(), fixture.Claims);
        using var accepted = admission.Lease;
        if (range == "accepted") Assert.NotNull(accepted);
        else Assert.Equal(ResourceLeaseDenial.HostUnavailable, admission.Denial);
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, fixture.Manager.TryAcquire(Guid.NewGuid(), [new("unrelated", ResourceClaimMode.Reference)]).Denial);
    }

    [Fact(DisplayName = "An Accepted Snapshot Write Retains The Lifecycle Write And No New Completion Can Start After Close Permission")]
    public void KeepsLifecycleWrites()
    {
        var fixture = new SnapshotCompletionFixture(); fixture.Publish();
        using var accepted = fixture.Manager.TryCompleteSnapshots(fixture.Permit(), fixture.Claims).Lease; Assert.NotNull(accepted);
        fixture.Index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations, []);
        fixture.Index.PublishVerified(fixture.Index.Current.Revision, fixture.Index.Current.Revision + 1, []);
        var closing = Guid.NewGuid();
        Assert.Equal("accepted_writes_pending", fixture.Lifecycle.RequestClose(closing, Guid.NewGuid()).ReasonCode);
        accepted.Dispose(); Assert.NotNull(fixture.Lifecycle.RequestClose(closing, Guid.NewGuid()).Permit);
        fixture.SetOwnHolds(); fixture.Publish();
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, fixture.Manager.TryCompleteSnapshots(fixture.Permit(), fixture.Claims).Denial);
    }
}
