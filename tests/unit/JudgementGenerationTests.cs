using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class JudgementGenerationTests
{
    [Fact(DisplayName = "Recorded Owner Replay Accepts Only Its Original Generations And Preserves The First Business Decision")]
    public void BindsOriginalGenerations()
    {
        var attempt = Guid.NewGuid(); var definition = new AutomationJudgement(AutomationJudgementLanguage.JavaScript, [], [], "source");
        var live = new AttemptBusinessJudgement(attempt, definition); var replay = AttemptBusinessJudgement.Replay(attempt, definition, live.InputIdentity.OwnerId);
        var first = Assert.IsType<JudgementInvocation>(live.TryBeginPeriodic()); Assert.Equal(first, replay.TryBeginPeriodic());
        Assert.False(live.Complete(first, new(BusinessOutcome.Unverified, ""))); Assert.False(replay.Complete(first, new(BusinessOutcome.Unverified, "")));
        live.RequestFinal(); replay.RequestFinal(); var final = Assert.IsType<JudgementInvocation>(live.TryBeginFinal());
        Assert.Equal(final, replay.TryBeginFinal()); Assert.False(replay.Complete(first, new(BusinessOutcome.Failed, "stale")));
        Assert.True(live.Complete(final, new(BusinessOutcome.Partial, "verified subset")));
        Assert.True(replay.Complete(final, new(BusinessOutcome.Partial, "verified subset")));
        Assert.Equal(live.Decision, replay.Decision); Assert.True(replay.FinalCompleted);
        Assert.Throws<ArgumentException>(() => AttemptBusinessJudgement.Replay(attempt, definition, Guid.Empty));
    }

    [Fact(DisplayName = "Busy Periodic Calls Do Not Allocate Or Capture Another Generation")]
    public void SkipsBusyGenerations()
    {
        var judge = Script(); var first = Assert.IsType<JudgementInvocation>(judge.TryBeginPeriodic());
        Assert.Equal(1, first.Generation); Assert.Equal(judge.AttemptId, first.AttemptId); Assert.False(first.IsFinal);
        Assert.True(judge.IsBusy);
        for (var index = 0; index < 10; index++) Assert.Null(judge.TryBeginPeriodic());
        Assert.False(judge.Complete(first, new(BusinessOutcome.Unverified, "still running")));
        Assert.False(judge.IsBusy);
        var next = Assert.IsType<JudgementInvocation>(judge.TryBeginPeriodic()); Assert.Equal(2, next.Generation);
    }

    [Fact(DisplayName = "Busy Final Requests Queue Exactly One Final Call Without Capturing Until The Periodic Call Finishes")]
    public void QueuesFinalWork()
    {
        var judge = Script(); var periodic = Assert.IsType<JudgementInvocation>(judge.TryBeginPeriodic());
        for (var index = 0; index < 10; index++) { judge.RequestFinal(); Assert.Null(judge.TryBeginFinal()); }
        Assert.Null(judge.TryBeginPeriodic());
        Assert.True(judge.Complete(periodic, new(BusinessOutcome.Partial, "accepted partial")));
        var decision = judge.Decision;
        var final = Assert.IsType<JudgementInvocation>(judge.TryBeginFinal());
        Assert.True(final.IsFinal); Assert.Equal(2, final.Generation);
        judge.RequestFinal(); Assert.Null(judge.TryBeginFinal());
        Assert.False(judge.Complete(final, new(BusinessOutcome.Failed, "later failure")));
        Assert.Equal(decision, judge.Decision); Assert.True(judge.FinalCompleted); Assert.False(judge.IsBusy);
        judge.RequestFinal(); Assert.Null(judge.TryBeginFinal()); Assert.Null(judge.TryBeginPeriodic());
    }

    [Theory(DisplayName = "Wrong Invocation Identity Cannot Change Decision Or Release The Current Worker")]
    [InlineData("attempt")]
    [InlineData("generation")]
    [InlineData("final")]
    [InlineData("owner")]
    public void RejectsForeignInvocations(string changed)
    {
        var judge = Script(); var call = Assert.IsType<JudgementInvocation>(judge.TryBeginPeriodic());
        var foreign = changed switch { "attempt" => call with { AttemptId = Guid.NewGuid() },
            "generation" => call with { Generation = call.Generation + 1 }, "owner" => call with { OwnerId = Guid.NewGuid() },
            _ => call with { IsFinal = true } };
        Assert.False(judge.Complete(foreign, new(BusinessOutcome.Succeeded, "foreign")));
        Assert.False(judge.CompleteFault(foreign)); Assert.True(judge.IsBusy); Assert.Null(judge.Decision);
        Assert.True(judge.Complete(call, new(BusinessOutcome.Failed, "current")));
        Assert.Equal("current", judge.Decision!.Reason); Assert.False(judge.IsBusy);
    }

    [Fact(DisplayName = "A Replayed Old Completion Cannot Consume A Newer Generation Or Publish A Second Terminal")]
    public void RejectsOldCompletions()
    {
        var judge = Script(); var old = Assert.IsType<JudgementInvocation>(judge.TryBeginPeriodic());
        Assert.True(judge.CompleteFault(old));
        var current = Assert.IsType<JudgementInvocation>(judge.TryBeginPeriodic());
        Assert.False(judge.Complete(old, new(BusinessOutcome.Succeeded, "old"))); Assert.True(judge.IsBusy);
        Assert.True(judge.Complete(current, new(BusinessOutcome.Succeeded, "current")));
        Assert.False(judge.Complete(current, new(BusinessOutcome.Failed, "duplicate")));
        Assert.Equal("current", judge.Decision!.Reason); Assert.Null(judge.TryBeginPeriodic());
    }

    [Theory(DisplayName = "The First Script Terminal And Its Reason Survive Different Later Final Results")]
    [InlineData(BusinessOutcome.Succeeded)]
    [InlineData(BusinessOutcome.Partial)]
    [InlineData(BusinessOutcome.Failed)]
    public void KeepsFirstTerminal(BusinessOutcome first)
    {
        var judge = Script(); var periodic = Assert.IsType<JudgementInvocation>(judge.TryBeginPeriodic());
        Assert.True(judge.Complete(periodic, new(first, "first evidence"))); var decision = judge.Decision;
        judge.RequestFinal(); var final = Assert.IsType<JudgementInvocation>(judge.TryBeginFinal());
        Assert.False(judge.Complete(final, new(first == BusinessOutcome.Failed ? BusinessOutcome.Succeeded : BusinessOutcome.Failed, "later")));
        Assert.Equal(decision, judge.Decision); Assert.True(judge.FinalCompleted);
    }

    [Fact(DisplayName = "A Final Worker Fault Completes The Final Request Without Replacing Previously Accepted Evidence")]
    public void CollectsFinalFaults()
    {
        var judge = Script(); var periodic = Assert.IsType<JudgementInvocation>(judge.TryBeginPeriodic());
        Assert.True(judge.Complete(periodic, new(BusinessOutcome.Succeeded, "original"))); var decision = judge.Decision;
        judge.RequestFinal(); Assert.True(judge.CompleteFault(Assert.IsType<JudgementInvocation>(judge.TryBeginFinal())));
        Assert.True(judge.FinalCompleted); Assert.Equal(decision, judge.Decision);
        judge.RequestFinal(); Assert.Null(judge.TryBeginFinal());
    }

    [Fact(DisplayName = "No Result Or Fault Keeps Business Unverified And Still Collects The Final Worker")]
    public void PreservesUnverifiedBusiness()
    {
        var judge = Script(); var periodic = Assert.IsType<JudgementInvocation>(judge.TryBeginPeriodic());
        Assert.True(judge.CompleteFault(periodic)); Assert.Equal(BusinessOutcome.Unverified, judge.Business);
        judge.RequestFinal(); var final = Assert.IsType<JudgementInvocation>(judge.TryBeginFinal());
        Assert.False(judge.Complete(final, new(BusinessOutcome.Unverified, "no business evidence")));
        Assert.True(judge.FinalCompleted); Assert.Null(judge.Decision); Assert.False(judge.IsBusy);
    }

    [Theory(DisplayName = "Cancelled Workers Must Be Collected But Cannot Publish Their Late Result Or Schedule Final Work")]
    [InlineData(false)]
    [InlineData(true)]
    public void DiscardsCancelledResults(bool final)
    {
        var judge = Script(); if (final) judge.RequestFinal();
        var call = Assert.IsType<JudgementInvocation>(final ? judge.TryBeginFinal() : judge.TryBeginPeriodic());
        judge.Cancel(); Assert.True(judge.IsCancelled); Assert.True(judge.IsBusy);
        judge.RequestFinal(); Assert.Null(judge.TryBeginPeriodic()); Assert.Null(judge.TryBeginFinal());
        Assert.False(judge.Complete(call, new(BusinessOutcome.Succeeded, "late success")));
        Assert.False(judge.IsBusy); Assert.Null(judge.Decision); Assert.Equal(BusinessOutcome.Unverified, judge.Business);
        Assert.False(judge.FinalCompleted); Assert.Null(judge.TryBeginFinal());
    }

    [Fact(DisplayName = "Cancellation Preserves An Accepted Terminal While Discarding A Late Final Fault")]
    public void PreservesCancelledTerminals()
    {
        var judge = Script(); var periodic = Assert.IsType<JudgementInvocation>(judge.TryBeginPeriodic());
        judge.Complete(periodic, new(BusinessOutcome.Partial, "accepted")); var decision = judge.Decision;
        judge.RequestFinal(); var final = Assert.IsType<JudgementInvocation>(judge.TryBeginFinal()); judge.Cancel();
        Assert.False(judge.CompleteFault(final)); Assert.Equal(decision, judge.Decision); Assert.False(judge.IsBusy);
    }

    [Theory(DisplayName = "A Worker Cannot Supply Host Lifecycle Outcomes Or An Unknown Business Status")]
    [InlineData(BusinessOutcome.Cancelled)]
    [InlineData(BusinessOutcome.NotApplicable)]
    [InlineData((BusinessOutcome)999)]
    public void RejectsHostOutcomes(BusinessOutcome outcome)
    {
        var judge = Script(); var call = Assert.IsType<JudgementInvocation>(judge.TryBeginPeriodic());
        Assert.Throws<ArgumentException>(() => judge.Complete(call, new(outcome, "invalid")));
        Assert.Null(judge.Decision); Assert.True(judge.IsBusy); Assert.True(judge.CompleteFault(call));
        Assert.Equal(BusinessOutcome.Unverified, judge.Business);
    }

    [Theory(DisplayName = "Partial Requires A Reason And Invalid Output Does Not Pretend The Worker Was Collected")]
    [InlineData("")]
    [InlineData(" ")]
    public void ValidatesPartialReasons(string reason)
    {
        var judge = Script(); judge.RequestFinal(); var call = Assert.IsType<JudgementInvocation>(judge.TryBeginFinal());
        Assert.Throws<ArgumentException>(() => judge.Complete(call, new(BusinessOutcome.Partial, reason)));
        Assert.True(judge.IsBusy); Assert.False(judge.FinalCompleted);
        Assert.True(judge.CompleteFault(call)); Assert.True(judge.FinalCompleted); Assert.Null(judge.Decision);
    }

    [Fact(DisplayName = "Final Work Requires An Explicit Request And Cannot Be Restarted After Completion")]
    public void RequiresFinalRequest()
    {
        var judge = Script(); Assert.Null(judge.TryBeginFinal()); judge.RequestFinal();
        var final = Assert.IsType<JudgementInvocation>(judge.TryBeginFinal());
        Assert.Equal(1, final.Generation); Assert.True(judge.Complete(final, new(BusinessOutcome.Succeeded, "final")));
        for (var index = 0; index < 10; index++) judge.RequestFinal();
        Assert.True(judge.FinalCompleted); Assert.Null(judge.TryBeginFinal()); Assert.Null(judge.TryBeginPeriodic());
    }

    [Fact(DisplayName = "Concurrent Final Requests Still Issue Only One Invocation")]
    public void IssuesSingleFinal()
    {
        var judge = Script(); var calls = new System.Collections.Concurrent.ConcurrentBag<JudgementInvocation>();
        Parallel.For(0, 32, _ => { judge.RequestFinal(); if (judge.TryBeginFinal() is { } call) calls.Add(call); });
        var final = Assert.Single(calls); Assert.Equal(1, final.Generation); Assert.True(final.IsFinal);
        Assert.True(judge.CompleteFault(final)); Assert.True(judge.FinalCompleted); Assert.Null(judge.TryBeginFinal());
    }

    private static AttemptBusinessJudgement Script() => new(Guid.NewGuid(), new(AutomationJudgementLanguage.JavaScript, [], [], "source"));

    [Fact(DisplayName = "Different Steps Of The Same Attempt Cannot Consume Each Others Identical Worker Generation")]
    public void BindsStepGenerations()
    {
        var attempt = Guid.NewGuid(); var definition = new AutomationJudgement(AutomationJudgementLanguage.JavaScript, [], [], "source");
        var first = new AttemptBusinessJudgement(attempt, definition); var second = new AttemptBusinessJudgement(attempt, definition);
        var old = Assert.IsType<JudgementInvocation>(first.TryBeginPeriodic()); var current = Assert.IsType<JudgementInvocation>(second.TryBeginPeriodic());
        Assert.Equal(old.AttemptId, current.AttemptId); Assert.Equal(old.Generation, current.Generation); Assert.NotEqual(old.OwnerId, current.OwnerId);
        first.Cancel();
        Assert.False(second.Complete(old, new(BusinessOutcome.Succeeded, "old step"))); Assert.False(second.CompleteFault(old));
        Assert.True(second.IsBusy); Assert.Equal(BusinessOutcome.Unverified, second.Business);
        Assert.True(second.Complete(current, new(BusinessOutcome.Partial, "actual step")));
        Assert.Equal(BusinessOutcome.Partial, second.Business); Assert.False(second.IsBusy);
    }

    [Fact(DisplayName = "A Previous Steps Keyword Line Cannot Enter The Next Steps Accumulator Within The Same Attempt")]
    public void IsolatesKeywordSteps()
    {
        var attempt = Guid.NewGuid(); var definition = new AutomationJudgement(AutomationJudgementLanguage.Keywords, ["first,last"], [], null);
        var first = new AttemptBusinessJudgement(attempt, definition); var next = new AttemptBusinessJudgement(attempt, definition);
        Assert.Null(next.ObserveLine(first.InputIdentity, "first")); Assert.Null(next.ObserveLine(next.InputIdentity, "last"));
        Assert.Equal(BusinessOutcome.Unverified, next.Business);
        Assert.Equal(BusinessOutcome.Succeeded, next.ObserveLine(next.InputIdentity, "first")!.Outcome);
        Assert.Equal(BusinessOutcome.Unverified, first.Business);
    }
}
