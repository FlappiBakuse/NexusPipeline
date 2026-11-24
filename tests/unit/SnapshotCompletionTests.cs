using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using Xunit;
using System.Collections.Immutable;

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
        var holds = fixture.Index.Current.ResourceHolds.ToArray(); var revision = fixture.Index.Current.Revision;
        using (var accepted = fixture.Manager.TryCompleteSnapshots(permit, fixture.Claims).Lease)
        {
            Assert.NotNull(accepted);
            Assert.Equal(ResourceLeaseDenial.Conflict, fixture.Manager.TryCompleteSnapshots(permit, fixture.Claims).Denial);
        }
        Assert.Equal(holds, fixture.Index.Current.ResourceHolds);
        using var retry = fixture.Manager.TryCompleteSnapshots(permit, fixture.Claims).Lease;
        Assert.NotNull(retry); Assert.Equal(permit.Completion.CompletionOperationId, fixture.Permit().Completion.CompletionOperationId);
        Assert.Equal(revision, fixture.Index.Current.Revision);
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

    [Theory(DisplayName = "Maintenance Draining Admits Only The Complete Mutation Range Held At Its Original Freeze")]
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
        using var drain = fixture.Lifecycle.BeginCompletionDrain(Guid.NewGuid());
        Assert.NotNull(drain);
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

// The port supplies Core evidence assertions only; it does not model native stop, journals on disk, or SQL.
internal sealed class SnapshotCompletionFixture : IRecoveryRegistry, ISnapshotCompletionEvidence
{
    internal const string SlotA = "configuration:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa:main";
    internal const string SlotB = "configuration:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa:extra";
    internal const string Other = "automation:fixture-script";
    private RecoveryRegistrySnapshot _state;
    internal RegisteredJournal Parent { get; private set; }
    internal SnapshotCompletionProjection Completion { get; set; }
    internal IReadOnlyList<SnapshotCompletionProjection>? Supplied { get; set; }
    internal RecoveryIndex Index { get; } = new();
    internal HostLifecycle Lifecycle { get; }
    internal ResourceLeaseManager Manager { get; }
    internal RecoveryCoordinator Coordinator { get; }
    internal List<string> Events { get; } = [];
    internal Action? DuringVerification { get; set; }
    internal Action? DuringRead { get; set; }
    internal Action? DuringPublish { get; set; }
    internal bool RejectEvidence { get; set; }
    internal bool ReturnUnreadyPublication { get; set; }
    internal bool ReturnUnadvancedPublication { get; set; }
    internal int PublishCalls { get; private set; }
    internal ImmutableArray<ResourceClaim> Claims => Completion.SlotKeys.Select(key => new ResourceClaim(key, ResourceClaimMode.Mutation)).ToImmutableArray();

    internal SnapshotCompletionFixture()
    {
        var digest = new string('a', 64);
        Parent = new(Guid.NewGuid(), Guid.NewGuid(), JournalKind.RunAttempt, JournalPhase.Prepared, 3,
            $"blobs/plans/{digest}.json", digest, [SlotA, SlotB, Other]);
        _state = new(4, false, [Parent], Isolations(Parent), []);
        Completion = new(Parent.OperationId, Parent.JournalId, Parent.Revision, Parent.PlanDigest, Guid.NewGuid(), Guid.NewGuid(),
            "snapshot-subject", ConfigurationRunPhase.Interrupted, 2, new('b', 64), [SlotA, SlotB]);
        Lifecycle = new(Index, TimeProvider.System); Manager = new(Lifecycle); Coordinator = new(this, this, Index);
        SetOwnHolds();
    }

    internal void SetOwnHolds(params string[] keys) => Index.UpdateResourceHolds(RecoveryHoldOwner.ConfigurationOperations,
        (keys.Length == 0 ? new[] { SlotA, SlotB } : keys).Select(key =>
            new RecoveryResourceHold(Parent.OperationId, key, "configuration_operation_pending")).ToArray());

    internal void ReplaceParent(RegisteredJournal parent, bool updateCompletion = false)
    {
        Parent = parent;
        _state = _state with { Revision = _state.Revision + 1, Journals = [parent], Isolations = Isolations(parent) };
        if (updateCompletion) Completion = Completion with { ParentJournalId = parent.JournalId, ParentOperationId = parent.OperationId,
            ParentRevision = parent.Revision, ParentPlanDigest = parent.PlanDigest };
    }

    internal void SetIsolation(ResourceClaimMode mode) => _state = _state with
    {
        Revision = _state.Revision + 1,
        Isolations = _state.Isolations.Select(item => item.ResourceKey == SlotA ? item with { Mode = mode } : item).ToArray()
    };

    internal void AddForeignIsolation()
    {
        var foreign = Parent with { JournalId = Guid.NewGuid(), OperationId = Guid.NewGuid(), ResourceKeys = [SlotA] };
        _state = _state with { Revision = _state.Revision + 1, Journals = [Parent, foreign],
            Isolations = _state.Isolations.Append(new(SlotA, foreign.JournalId, "recovery_pending")).ToArray() };
    }

    internal RecoveryProjection Publish() => Coordinator.VerifyAndPublish();
    internal SnapshotCompletionPermit Permit() => Assert.IsType<SnapshotCompletionPermit>(Index.AuthorizeSnapshotCompletion(
        Completion.ParentJournalId, Completion.SessionId, Completion.CompletionOperationId));

    public RecoveryRegistrySnapshot ReadSnapshot() => _state;
    public void VerifyAll(IReadOnlyList<RegisteredJournal> registered)
    {
        Events.Add("verify");
        if (RejectEvidence) throw new InvalidOperationException("fixture_evidence_rejected");
        DuringVerification?.Invoke();
    }
    public IReadOnlyList<SnapshotCompletionProjection> ReadPendingCompletions(RecoveryRegistrySnapshot verified)
    {
        Assert.Equal("verify", Events[^1]); Events.Add("completion"); DuringRead?.Invoke();
        return Supplied ?? [Completion];
    }
    public RecoveryRegistrySnapshot PublishVerified(long expectedRevision, long minimumRevision)
    {
        Assert.Equal(expectedRevision, _state.Revision); Events.Add("publish"); PublishCalls++; DuringPublish?.Invoke();
        return _state = _state with { IsReady = !ReturnUnreadyPublication,
            Revision = ReturnUnadvancedPublication ? _state.Revision : Math.Max(_state.Revision + 1, minimumRevision) };
    }

    private static ResourceIsolation[] Isolations(RegisteredJournal parent) => parent.Phase == JournalPhase.Committed ? [] :
        parent.ResourceKeys.Select(key => new ResourceIsolation(key, parent.JournalId, "recovery_pending")).ToArray();
}
