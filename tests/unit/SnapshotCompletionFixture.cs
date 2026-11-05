using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using Xunit;

namespace NexusPipeline.CoreTests;

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
