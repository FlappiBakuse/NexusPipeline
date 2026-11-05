using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class EnteredRecoveryTests
{
    [Theory(DisplayName = "Entered Recovery Stops And Collects Before Restoring And Commits Interrupted Without Replacing Verified Business")]
    [InlineData(JournalPhase.Prepared)]
    [InlineData(JournalPhase.Applying)]
    [InlineData(JournalPhase.Applied)]
    public void RestoresEnteredAttempts(JournalPhase phase)
    {
        var fixture = new Fixture(phase);
        Assert.Equal(JournalRecoveryResult.Restored, new EnteredAttemptRecovery(fixture).Recover());
        Assert.Equal(phase == JournalPhase.Applied ? new[] { "freeze", "stop", "collect", "restore", "commit" } :
            new[] { "freeze", "stop", "collect", "applied", "restore", "commit" }, fixture.Events);
        Assert.Equal(EngineOutcome.Succeeded, fixture.State.Facts.Engine);
        Assert.Equal(BusinessOutcome.Partial, fixture.State.Facts.Business);
        Assert.Equal(LifecycleOutcome.Interrupted, fixture.State.Facts.Lifecycle);
        Assert.Equal(RecoveryOutcome.Restored, fixture.State.Facts.Recovery);
        Assert.Equal("host_interrupted", fixture.State.Facts.ReasonCode);
    }

    [Fact(DisplayName = "An Entered Recovery With No Terminal Engine Fact Records Unknown Without Promoting Unverified Business")]
    public void PreservesUnknownEngine()
    {
        var fixture = new Fixture() { State = Fixture.Initial() with { Facts = Fixture.Facts() with
            { Engine = EngineOutcome.Running, Business = BusinessOutcome.Unverified } } };
        Assert.Equal(JournalRecoveryResult.Restored, new EnteredAttemptRecovery(fixture).Recover());
        Assert.Equal(EngineOutcome.Unknown, fixture.State.Facts.Engine);
        Assert.Equal(BusinessOutcome.Unverified, fixture.State.Facts.Business);
    }

    [Theory(DisplayName = "Missing Real Recovery Evidence Keeps Isolation And The Existing Engine And Business Facts")]
    [InlineData("stop", "attempt_writer_stop_unconfirmed")]
    [InlineData("collect", "attempt_workers_uncollected")]
    [InlineData("restore", "attempt_configuration_not_restored")]
    public void RequiresRecoveryEvidence(string failure, string reason)
    {
        var fixture = new Fixture() { Failure = failure }; var before = fixture.State.Facts;
        Assert.Equal(JournalRecoveryResult.Quarantined, new EnteredAttemptRecovery(fixture).Recover());
        Assert.Equal(before.Engine, fixture.State.Facts.Engine); Assert.Equal(before.Business, fixture.State.Facts.Business);
        Assert.Equal(RecoveryOutcome.Quarantined, fixture.State.Facts.Recovery); Assert.Equal(reason, fixture.State.Facts.ReasonCode);
        Assert.DoesNotContain("commit", fixture.Events);
        if (failure is "stop" or "collect") Assert.DoesNotContain("restore", fixture.Events);
        if (failure == "stop") Assert.DoesNotContain("applied", fixture.Events);
    }

    [Theory(DisplayName = "A Sealed Or Unentered Attempt Cannot Use The Entered Recovery Owner")]
    [InlineData(EffectGateState.Sealed)]
    [InlineData(EffectGateState.Unentered)]
    public void RejectsWrongPhases(EffectGateState gate)
    {
        var fixture = new Fixture() { State = Fixture.Initial() with { Gate = gate } };
        Assert.Throws<InvalidOperationException>(() => new EnteredAttemptRecovery(fixture).Recover());
        Assert.Empty(fixture.Events);
    }

    [Fact(DisplayName = "An Ordinary Recovery Cannot Clear An Existing Quarantine While Explicit Recovery Still Requires All Real Evidence")]
    public void PreservesExistingQuarantine()
    {
        var fixture = new Fixture(JournalPhase.Quarantined);
        Assert.Equal(JournalRecoveryResult.Quarantined, new EnteredAttemptRecovery(fixture).Recover()); Assert.Empty(fixture.Events);
        Assert.Equal(JournalRecoveryResult.Restored, new EnteredAttemptRecovery(fixture).Recover(true));
    }

    [Fact(DisplayName = "A Stopped Collected Parent Can Defer Its Bound Child Recovery Without Committing Or Changing The Child Owners Cursor")]
    public void DefersBoundChildren()
    {
        var fixture = new Fixture(); fixture.State = fixture.State with { CanDeferConfigurationCompletion = true };
        Assert.Equal(JournalRecoveryResult.PendingConfigurationCompletion, new EnteredAttemptRecovery(fixture).Recover());
        Assert.Equal(new[] { "freeze", "stop", "collect", "applied", "pending" }, fixture.Events);
        Assert.Equal(JournalPhase.Applied, fixture.State.Phase);
        Assert.Equal(LifecycleOutcome.Running, fixture.State.Facts.Lifecycle);
        Assert.False(fixture.State.IsConfigurationRestored);
    }

    [Theory(DisplayName = "Explicit Quarantine Recovery Requires The Child Restoration Result Before The Guarded Applied Transition")]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiresChildRestoration(bool restoreFails)
    {
        var fixture = new Fixture(JournalPhase.Quarantined) { Failure = restoreFails ? "restore" : null };
        var result = new EnteredAttemptRecovery(fixture).Recover(true);
        Assert.Equal(restoreFails ? JournalRecoveryResult.Quarantined : JournalRecoveryResult.Restored, result);
        Assert.Equal(restoreFails ? new[] { "freeze", "stop", "collect", "restore", "quarantine" } :
            new[] { "freeze", "stop", "collect", "restore", "applied", "commit" }, fixture.Events);
    }

    [Fact(DisplayName = "A Committed Entered Attempt Reconciles Its Recorded Facts Without Running Stop Or Restore Again")]
    public void ReconcilesCommittedFacts()
    {
        var fixture = new Fixture(JournalPhase.Committed);
        fixture.State = fixture.State with { DatabaseAgrees = false, Writers = WriterState.Stopped, IsWriterObservationComplete = true,
            AreWorkersCollected = true, IsConfigurationRestored = true, CanReconcileRecordedCommit = true,
            Facts = fixture.State.Facts with { Lifecycle = LifecycleOutcome.Interrupted, Recovery = RecoveryOutcome.Restored } };
        Assert.Equal(JournalRecoveryResult.AlreadyCompleted, new EnteredAttemptRecovery(fixture).Recover());
        Assert.Equal(new[] { "reconcile" }, fixture.Events);
    }

    [Theory(DisplayName = "A Recovery Callback Cannot Replace The Original Attempt Or Previously Verified Outcomes")]
    [InlineData("identity")]
    [InlineData("business")]
    [InlineData("engine")]
    public void RejectsChangedOutcomes(string change)
    {
        var fixture = new Fixture() { ChangeAfterStop = change };
        Assert.Throws<InvalidOperationException>(() => new EnteredAttemptRecovery(fixture).Recover());
        Assert.Equal(new[] { "freeze", "stop" }, fixture.Events);
    }

    private sealed class Fixture : IEnteredRecoverySession, IEnteredConfigurationCompletion
    {
        internal EnteredAttemptInspection State { get; set; }
        internal List<string> Events { get; } = [];
        internal string? Failure { get; init; }
        internal string? ChangeAfterStop { get; init; }
        internal Fixture(JournalPhase phase = JournalPhase.Applying) => State = Initial() with { Phase = phase };
        internal static AttemptFacts Facts() => new(Guid.NewGuid(), Guid.NewGuid(), 2, EngineOutcome.Succeeded, BusinessOutcome.Partial,
            LifecycleOutcome.Running, RecoveryOutcome.Pending, "execution_running");
        internal static EnteredAttemptInspection Initial() => new(JournalPhase.Applying, true, EffectGateState.Entered,
            false, WriterState.Unknown, false, false, false, Facts());
        public EnteredAttemptInspection Inspect() => State;
        public void FreezeDispatch() => Events.Add("freeze");
        public void StopWriters()
        {
            Events.Add("stop");
            if (Failure != "stop") State = State with { Writers = WriterState.Stopped, IsWriterObservationComplete = true };
            State = State with { Facts = ChangeAfterStop switch
            {
                "identity" => State.Facts with { AttemptId = Guid.NewGuid() },
                "business" => State.Facts with { Business = BusinessOutcome.Succeeded },
                "engine" => State.Facts with { Engine = EngineOutcome.Failed },
                _ => State.Facts
            } };
        }
        public void CollectWorkers() { Events.Add("collect"); if (Failure != "collect") State = State with { AreWorkersCollected = true }; }
        public void MarkApplied() { Events.Add("applied"); State = State with { Phase = JournalPhase.Applied }; }
        public void RestoreConfiguration() { Events.Add("restore"); if (Failure != "restore") State = State with { IsConfigurationRestored = true }; }
        public void CommitInterrupted(AttemptFacts facts) { Events.Add("commit"); State = State with { Phase = JournalPhase.Committed, Facts = facts }; }
        public void Quarantine(AttemptFacts facts) { Events.Add("quarantine"); State = State with { Phase = JournalPhase.Quarantined, Facts = facts }; }
        public void ReconcileRecordedCommit() { Events.Add("reconcile"); State = State with { DatabaseAgrees = true }; }
        public void VerifyPendingCompletion() => Events.Add("pending");
    }
}
