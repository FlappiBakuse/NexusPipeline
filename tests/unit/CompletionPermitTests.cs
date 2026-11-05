using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class CompletionPermitTests
{
    [Theory(DisplayName = "Verified Exact Snapshot Completion Preserves Every Isolation And Does Not Authorize Ordinary Writes")]
    [InlineData(ConfigurationRunPhase.Completed)]
    [InlineData(ConfigurationRunPhase.Interrupted)]
    public void PreservesResourceIsolation(ConfigurationRunPhase terminal)
    {
        var fixture = new SnapshotCompletionFixture(); fixture.Completion = fixture.Completion with { Terminal = terminal };
        var before = fixture.ReadSnapshot().Isolations.ToArray(); fixture.Publish(); var permit = fixture.Permit();
        Assert.Equal(new[] { "verify", "completion", "publish" }, fixture.Events);
        Assert.True(permit.Completion.SlotKeys.SequenceEqual(fixture.Completion.SlotKeys.Order(StringComparer.Ordinal)));
        using var lease = fixture.Manager.TryCompleteSnapshots(permit, fixture.Claims.Reverse().ToArray()).Lease;
        Assert.NotNull(lease); Assert.Equal(fixture.Parent.OperationId, lease.OperationId);
        Assert.Equal(before, fixture.Index.Current.Isolations);
        Assert.Contains(fixture.Index.Current.Isolations, item => item.ResourceKey == SnapshotCompletionFixture.Other);
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, fixture.Manager.TryAcquire(Guid.NewGuid(), fixture.Claims).Denial);
        Assert.Null(fixture.Index.AuthorizeContinuation(RecoveryHoldOwner.ConfigurationOperations, fixture.Parent.OperationId,
            fixture.Claims.Select(item => item.ResourceKey).ToHashSet(StringComparer.Ordinal)));
    }

    [Theory(DisplayName = "Invalid Or Unbound Completion Evidence Cannot Be Published As Authority")]
    [InlineData("parent-operation")]
    [InlineData("parent-journal")]
    [InlineData("parent-revision")]
    [InlineData("parent-digest")]
    [InlineData("session")]
    [InlineData("completion")]
    [InlineData("subject")]
    [InlineData("control-subject")]
    [InlineData("terminal")]
    [InlineData("unknown-terminal")]
    [InlineData("group-revision")]
    [InlineData("digest")]
    [InlineData("default-slots")]
    [InlineData("empty-slots")]
    [InlineData("duplicate-slots")]
    [InlineData("physical-slot")]
    [InlineData("script-slot")]
    [InlineData("settings-slot")]
    [InlineData("foreign-slot")]
    [InlineData("empty-binding")]
    public void RejectsUnboundEvidence(string fault)
    {
        var fixture = new SnapshotCompletionFixture();
        fixture.Completion = fault switch
        {
            "parent-operation" => fixture.Completion with { ParentOperationId = Guid.NewGuid() },
            "parent-journal" => fixture.Completion with { ParentJournalId = Guid.NewGuid() },
            "parent-revision" => fixture.Completion with { ParentRevision = fixture.Parent.Revision + 1 },
            "parent-digest" => fixture.Completion with { ParentPlanDigest = new('c', 64) },
            "session" => fixture.Completion with { SessionId = Guid.Empty },
            "completion" => fixture.Completion with { CompletionOperationId = Guid.Empty },
            "subject" => fixture.Completion with { SubjectId = " " },
            "control-subject" => fixture.Completion with { SubjectId = "subject\n" },
            "terminal" => fixture.Completion with { Terminal = ConfigurationRunPhase.Running },
            "unknown-terminal" => fixture.Completion with { Terminal = (ConfigurationRunPhase)99 },
            "group-revision" => fixture.Completion with { GroupRevision = 0 },
            "digest" => fixture.Completion with { EvidenceDigest = new('G', 64) },
            "default-slots" => fixture.Completion with { SlotKeys = default },
            "empty-slots" => fixture.Completion with { SlotKeys = [] },
            "duplicate-slots" => fixture.Completion with { SlotKeys = [SnapshotCompletionFixture.SlotA, SnapshotCompletionFixture.SlotA] },
            "physical-slot" => fixture.Completion with { SlotKeys = ["filesystem:object:fixture"] },
            "script-slot" => fixture.Completion with { SlotKeys = ["configuration:script:fixture"] },
            "settings-slot" => fixture.Completion with { SlotKeys = ["settings:host"] },
            "foreign-slot" => fixture.Completion with { SlotKeys = ["configuration:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa:foreign"] },
            _ => fixture.Completion with { SlotKeys = ["configuration:00000000-0000-0000-0000-000000000000:main"] }
        };
        Assert.Throws<InvalidOperationException>(() => fixture.Publish());
        Assert.False(fixture.Index.Current.IsReady); Assert.Empty(fixture.Index.Current.SnapshotCompletions);
        Assert.Equal(0, fixture.PublishCalls);
    }

    [Theory(DisplayName = "A Committed Or Quarantined Parent Cannot Grant Snapshot Completion")]
    [InlineData(JournalPhase.Committed)]
    [InlineData(JournalPhase.Quarantined)]
    public void RejectsTerminalParents(JournalPhase phase)
    {
        var fixture = new SnapshotCompletionFixture(); fixture.ReplaceParent(fixture.Parent with { Phase = phase });
        Assert.Throws<InvalidOperationException>(() => fixture.Publish()); Assert.Empty(fixture.Index.Current.SnapshotCompletions);
    }

    [Fact(DisplayName = "A Parent Must Be A Run Attempt And Its Slot Isolation Must Authorize Mutation")]
    public void RequiresMutationParent()
    {
        var wrongKind = new SnapshotCompletionFixture(); wrongKind.ReplaceParent(wrongKind.Parent with { Kind = JournalKind.ConfigEdit });
        Assert.Throws<InvalidOperationException>(() => wrongKind.Publish());
        var reference = new SnapshotCompletionFixture(); reference.SetIsolation(ResourceClaimMode.Reference);
        Assert.Throws<InvalidOperationException>(() => reference.Publish());
    }

    [Theory(DisplayName = "Duplicate Pending Session Or Completion Identities Cannot Create Ambiguous Authority")]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectsAmbiguousIdentities(bool session)
    {
        var fixture = new SnapshotCompletionFixture();
        var other = session ? fixture.Completion with { CompletionOperationId = Guid.NewGuid() }
            : fixture.Completion with { SessionId = Guid.NewGuid() };
        fixture.Supplied = [fixture.Completion, other];
        Assert.Throws<InvalidOperationException>(() => fixture.Publish()); Assert.Empty(fixture.Index.Current.SnapshotCompletions);
    }

    [Theory(DisplayName = "Verification Failure Or A Registry Change At Any Evidence Boundary Cannot Expose A Permit")]
    [InlineData("verification")]
    [InlineData("before-read")]
    [InlineData("during-read")]
    [InlineData("during-publish")]
    public void RechecksRegistryEvidence(string point)
    {
        var fixture = new SnapshotCompletionFixture();
        void Change() => fixture.ReplaceParent(fixture.Parent with { Revision = fixture.Parent.Revision + 1 });
        if (point == "verification") fixture.RejectEvidence = true;
        if (point == "before-read") fixture.DuringVerification = Change;
        if (point == "during-read") fixture.DuringRead = Change;
        if (point == "during-publish") fixture.DuringPublish = Change;
        Assert.Throws<InvalidOperationException>(() => fixture.Publish()); Assert.False(fixture.Index.Current.IsReady);
        Assert.Empty(fixture.Index.Current.SnapshotCompletions);
        if (point is "verification" or "before-read") Assert.DoesNotContain("completion", fixture.Events);
    }

    [Fact(DisplayName = "An Evidence Owner Without The Explicit Port Cannot Grant Authority")]
    public void RequiresCompletionPort()
    {
        var fixture = new SnapshotCompletionFixture();
        new RecoveryCoordinator(fixture, new OrdinaryEvidence(), fixture.Index).VerifyAndPublish();
        Assert.Empty(fixture.Index.Current.SnapshotCompletions);
        Assert.Null(fixture.Index.AuthorizeSnapshotCompletion(fixture.Parent.JournalId, fixture.Completion.SessionId, fixture.Completion.CompletionOperationId));
    }

    [Theory(DisplayName = "Registry Publication Must Actually Become Ready And Advance Before Any Authority Can Be Issued")]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiresPublishedRegistry(bool unready)
    {
        var fixture = new SnapshotCompletionFixture
            { ReturnUnreadyPublication = unready, ReturnUnadvancedPublication = !unready };
        Assert.Throws<InvalidOperationException>(() => fixture.Publish());
        Assert.False(fixture.Index.Current.IsReady); Assert.Empty(fixture.Index.Current.SnapshotCompletions);
    }

    [Theory(DisplayName = "Every Publication Or Unknown Transition Revokes Previously Issued Permits")]
    [InlineData("unknown")]
    [InlineData("public-publish")]
    [InlineData("same-publish")]
    [InlineData("missing")]
    [InlineData("damaged")]
    [InlineData("parent-revision")]
    public void RevokesOldPermits(string change)
    {
        var fixture = new SnapshotCompletionFixture(); fixture.Publish(); var permit = fixture.Permit();
        if (change == "unknown") fixture.Index.BlockUnknown("fixture_unknown");
        if (change == "public-publish") fixture.Index.PublishVerified(fixture.Index.Current.Revision,
            fixture.Index.Current.Revision + 1, fixture.Index.Current.Isolations);
        if (change == "missing") fixture.Supplied = [];
        if (change == "damaged") fixture.Completion = fixture.Completion with { EvidenceDigest = "invalid" };
        if (change == "parent-revision") fixture.ReplaceParent(fixture.Parent with { Revision = fixture.Parent.Revision + 1 }, updateCompletion: true);
        if (change is "same-publish" or "missing" or "parent-revision") fixture.Publish();
        if (change == "damaged") Assert.Throws<InvalidOperationException>(() => fixture.Publish());
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, fixture.Manager.TryCompleteSnapshots(permit, fixture.Claims).Denial);
        if (change is "same-publish" or "parent-revision")
        {
            using var lease = fixture.Manager.TryCompleteSnapshots(fixture.Permit(), fixture.Claims).Lease; Assert.NotNull(lease);
        }
    }

    [Theory(DisplayName = "Reissued Authority Is Bound To The New Exact Completion Metadata And Cannot Reuse An Old Permit")]
    [InlineData("subject")]
    [InlineData("terminal")]
    [InlineData("group-revision")]
    [InlineData("digest")]
    public void BindsReissuedAuthority(string changed)
    {
        var fixture = new SnapshotCompletionFixture(); fixture.Publish(); var before = fixture.Permit();
        fixture.Completion = changed switch
        {
            "subject" => fixture.Completion with { SubjectId = "other-subject" },
            "terminal" => fixture.Completion with { Terminal = ConfigurationRunPhase.Completed },
            "group-revision" => fixture.Completion with { GroupRevision = 3 },
            _ => fixture.Completion with { EvidenceDigest = new('c', 64) }
        };
        fixture.Publish(); var current = fixture.Permit();
        Assert.NotEqual(before.Completion, current.Completion);
        Assert.Equal(ResourceLeaseDenial.HostUnavailable, fixture.Manager.TryCompleteSnapshots(before, fixture.Claims).Denial);
        using var lease = fixture.Manager.TryCompleteSnapshots(current, fixture.Claims).Lease; Assert.NotNull(lease);
    }

    [Theory(DisplayName = "Authorization Requires Every Exact Lookup Identity")]
    [InlineData("parent")]
    [InlineData("session")]
    [InlineData("operation")]
    public void RequiresExactIdentity(string changed)
    {
        var fixture = new SnapshotCompletionFixture(); fixture.Publish();
        Assert.Null(fixture.Index.AuthorizeSnapshotCompletion(changed == "parent" ? Guid.NewGuid() : fixture.Parent.JournalId,
            changed == "session" ? Guid.NewGuid() : fixture.Completion.SessionId,
            changed == "operation" ? Guid.NewGuid() : fixture.Completion.CompletionOperationId));
    }

    [Fact(DisplayName = "Published Completion Metadata Does Not Retain The Evidence Owners Mutable Result Collection")]
    public void FreezesCompletionMetadata()
    {
        var fixture = new SnapshotCompletionFixture(); var pending = new List<SnapshotCompletionProjection> { fixture.Completion };
        fixture.Supplied = pending; fixture.Publish(); var permit = fixture.Permit(); pending.Clear();
        Assert.Single(fixture.Index.Current.SnapshotCompletions);
        using var lease = fixture.Manager.TryCompleteSnapshots(permit, fixture.Claims).Lease; Assert.NotNull(lease);
    }

    private sealed class OrdinaryEvidence : IRecoveryEvidence
    {
        public void VerifyAll(IReadOnlyList<RegisteredJournal> registered) { }
    }
}
