using NexusPipeline.Core.Configuration;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class RecoveryCoordinatorTests
{
    [Fact(DisplayName = "Explicit Recovery Dependencies Execute The Parent Before An Earlier Listed Child")]
    public void OrdersDependentRecovery()
    {
        var fixture = new Fixture();
        var journals = fixture.ReadSnapshot().Journals;
        fixture.Dependencies[journals[0].JournalId] = [journals[^1].JournalId];
        fixture.Coordinator.RecoverAndPublish(fixture, fixture.Handlers);
        Assert.True(fixture.ExecutedKinds.IndexOf(journals[^1].Kind) < fixture.ExecutedKinds.IndexOf(journals[0].Kind));
        Assert.Equal(journals.Count, fixture.ExecutedKinds.Count);
    }

    [Theory(DisplayName = "Invalid Recovery Dependencies Reject The Whole Batch Before Any Effect")]
    [InlineData("missing")]
    [InlineData("self")]
    [InlineData("cycle")]
    [InlineData("duplicate")]
    public void RejectsInvalidDependencies(string fault)
    {
        var fixture = new Fixture(); var journals = fixture.ReadSnapshot().Journals;
        var first = journals[0].JournalId; var last = journals[^1].JournalId;
        fixture.Dependencies[first] = fault switch
        {
            "missing" => [Guid.NewGuid()], "self" => [first], "duplicate" => [last, last], _ => [last]
        };
        if (fault == "cycle") fixture.Dependencies[last] = [first];
        Assert.Throws<InvalidOperationException>(() => fixture.Coordinator.RecoverAndPublish(fixture, fixture.Handlers));
        Assert.Empty(fixture.ExecutedKinds); Assert.Equal(0, fixture.PublishCount);
        Assert.Equal(journals.Count, fixture.DisposedKinds.Count); Assert.False(fixture.Index.Current.IsReady);
    }

    [Theory(DisplayName = "Recovery Preparation Ownership Is Released When Later Preflight Fails Or The Batch Completes")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasesPreparedOwnership(bool lateFailure)
    {
        var fixture = new Fixture { RejectKind = lateFailure ? JournalKind.ExtensionApply : null };
        if (lateFailure) Assert.Throws<InvalidOperationException>(() => fixture.Coordinator.RecoverAndPublish(fixture, fixture.Handlers));
        else fixture.Coordinator.RecoverAndPublish(fixture, fixture.Handlers);
        var expected = fixture.ReadSnapshot().Journals.Select(value => value.Kind)
            .TakeWhile(value => !lateFailure || value != JournalKind.ExtensionApply).Reverse().ToArray();
        Assert.Equal(expected, fixture.DisposedKinds);
    }

    [Fact(DisplayName = "Admission Can Read Its Blocked Projection While Slow Physical Recovery Preparation Is Waiting")]
    public async Task ExposesBlockedProjection()
    {
        var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preflight = new WaitingPreflight(() => { started.SetResult(); release.Task.GetAwaiter().GetResult(); });
        var recovery = Task.Run(() => fixture.Coordinator.RecoverAndPublish(preflight, fixture.Handlers));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var blocked = await Task.Run(() => fixture.Index.IsBlocked(new HashSet<string>(), out _)).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(blocked);
        }
        finally { release.TrySetResult(); }
        await recovery.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(fixture.Index.Current.IsReady);
    }

    private sealed class WaitingPreflight(Action wait) : IRecoveryPreflight
    {
        public void VerifyRecognizedAll(IReadOnlyList<RegisteredJournal> registered) => wait();
    }

    [Fact(DisplayName = "All Kind Plans Are Prepared Read Only Before Any Recovery Work Can Execute")]
    public void PreparesBeforeEffects()
    {
        var fixture = new Fixture();
        var result = fixture.Coordinator.RecoverAndPublish(fixture, fixture.Handlers);
        Assert.Equal(Enum.GetValues<JournalKind>().Length, result.Count);
        Assert.All(result.Values, value => Assert.Equal(JournalRecoveryResult.NotExecuted, value));
        Assert.Equal(new[] { "preflight" }.Concat(Enumerable.Repeat("prepare", result.Count)).Concat(Enumerable.Repeat("execute", result.Count)).Append("verify"), fixture.Events);
        Assert.True(fixture.Index.Current.IsReady);
        Assert.Empty(fixture.Index.Current.Isolations);
    }

    [Fact(DisplayName = "An Unrecognized Later Plan Cannot Leave Earlier Journals Partially Recovered")]
    public void RejectsUnknownPlans()
    {
        var fixture = new Fixture { RejectKind = JournalKind.ExtensionApply };
        var before = fixture.ReadSnapshot();
        Assert.Throws<InvalidOperationException>(() => fixture.Coordinator.RecoverAndPublish(fixture, fixture.Handlers));
        Assert.DoesNotContain("execute", fixture.Events);
        Assert.Equal(before, fixture.ReadSnapshot());
        Assert.False(fixture.Index.Current.IsReady);
        Assert.Equal(0, fixture.PublishCount);
    }

    [Theory(DisplayName = "Missing Or Duplicate Kind Owners Fail Before Physical Preparation")]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiresKindOwners(bool duplicate)
    {
        var fixture = new Fixture();
        var handlers = duplicate ? fixture.Handlers.Append(fixture.Handlers[0]).ToArray() : fixture.Handlers[..^1];
        Assert.Throws<InvalidOperationException>(() => fixture.Coordinator.RecoverAndPublish(fixture, handlers));
        Assert.Empty(fixture.Events);
        Assert.False(fixture.Index.Current.IsReady);
    }

    [Fact(DisplayName = "A Registry Change During Read Only Preparation Prevents Every Prepared Effect")]
    public void RechecksPreparedRegistry()
    {
        var fixture = new Fixture { ChangeRevisionAfterPreparation = true };
        Assert.Throws<InvalidOperationException>(() => fixture.Coordinator.RecoverAndPublish(fixture, fixture.Handlers));
        Assert.DoesNotContain("execute", fixture.Events);
        Assert.Equal(0, fixture.PublishCount);
        Assert.False(fixture.Index.Current.IsReady);
    }

    [Fact(DisplayName = "Failed Final Verification Keeps Admission Blocked Without Undoing Recorded Recovery")]
    public void KeepsFailedVerification()
    {
        var fixture = new Fixture { RejectFinalVerification = true };
        Assert.Throws<InvalidOperationException>(() => fixture.Coordinator.RecoverAndPublish(fixture, fixture.Handlers));
        Assert.Equal(Enum.GetValues<JournalKind>().Length, fixture.Events.Count(item => item == "execute"));
        Assert.All(fixture.ReadSnapshot().Journals, journal => Assert.Equal(JournalPhase.Committed, journal.Phase));
        Assert.False(fixture.Index.Current.IsReady);
        Assert.Equal(0, fixture.PublishCount);
    }

    private sealed class Fixture : IRecoveryRegistry, IRecoveryEvidence, IRecoveryPreflight
    {
        private RecoveryRegistrySnapshot _state;
        public RecoveryIndex Index { get; } = new();
        public RecoveryCoordinator Coordinator { get; }
        public IJournalRecoveryHandler[] Handlers { get; }
        public List<string> Events { get; } = [];
        public List<JournalKind> DisposedKinds { get; } = [];
        public List<JournalKind> ExecutedKinds { get; } = [];
        public Dictionary<Guid, Guid[]> Dependencies { get; } = [];
        public JournalKind? RejectKind { get; init; }
        public bool ChangeRevisionAfterPreparation { get; init; }
        public bool RejectFinalVerification { get; init; }
        public int PublishCount { get; private set; }

        public Fixture()
        {
            var digest = new string('a', 64);
            var journals = Enum.GetValues<JournalKind>().Select(kind => new RegisteredJournal(Guid.NewGuid(), Guid.NewGuid(), kind,
                JournalPhase.Prepared, 1, $"blobs/plans/{digest}.json", digest, ["fixture:" + kind])).ToArray();
            _state = new(1, false, journals, journals.Select(item => new ResourceIsolation(item.ResourceKeys[0], item.JournalId, "recovery_pending")).ToArray(), []);
            Coordinator = new(this, this, Index);
            Handlers = Enum.GetValues<JournalKind>().Select(kind => (IJournalRecoveryHandler)new Handler(this, kind)).ToArray();
        }

        public RecoveryRegistrySnapshot ReadSnapshot() => _state;
        public void VerifyRecognizedAll(IReadOnlyList<RegisteredJournal> registered) => Events.Add("preflight");
        public void VerifyAll(IReadOnlyList<RegisteredJournal> registered)
        {
            Events.Add("verify");
            Assert.All(registered, item => Assert.Equal(JournalPhase.Committed, item.Phase));
            if (RejectFinalVerification) throw new InvalidOperationException("fixture_invalid_final_evidence");
        }
        public RecoveryRegistrySnapshot PublishVerified(long expectedRevision, long minimumRevision)
        {
            Assert.Equal(expectedRevision, _state.Revision);
            PublishCount++;
            return _state = _state with { IsReady = true, Revision = Math.Max(_state.Revision + 1, minimumRevision) };
        }

        private sealed class Handler(Fixture fixture, JournalKind kind) : IJournalRecoveryHandler
        {
            public JournalKind Kind => kind;
            public IJournalRecoveryWork Prepare(RegisteredJournal journal)
            {
                fixture.Events.Add("prepare");
                if (fixture.RejectKind == kind) throw new InvalidOperationException("fixture_unknown_typed_plan");
                if (fixture.ChangeRevisionAfterPreparation && kind == JournalKind.ExtensionApply)
                    fixture._state = fixture._state with { Revision = fixture._state.Revision + 1 };
                return new Work(fixture, journal);
            }
        }
        private sealed class Work(Fixture fixture, RegisteredJournal journal) : IJournalRecoveryWork
        {
            public IReadOnlyList<Guid> MustRunAfter => fixture.Dependencies.GetValueOrDefault(journal.JournalId) ?? [];
            public void Dispose() => fixture.DisposedKinds.Add(journal.Kind);
            public JournalRecoveryResult Execute()
            {
                fixture.Events.Add("execute");
                fixture.ExecutedKinds.Add(journal.Kind);
                fixture._state = fixture._state with
                {
                    Revision = fixture._state.Revision + 1,
                    Journals = fixture._state.Journals.Select(item => item.JournalId == journal.JournalId ? item with { Phase = JournalPhase.Committed, Revision = item.Revision + 1 } : item).ToArray(),
                    Isolations = fixture._state.Isolations.Where(item => item.JournalId != journal.JournalId).ToArray()
                };
                return JournalRecoveryResult.NotExecuted;
            }
        }
    }
}
