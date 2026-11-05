using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class BusinessJudgementTests
{
    [Theory(DisplayName = "Every Word In A Group Must Occur Across The Attempts Lines In Either Order")]
    [InlineData(",")]
    [InlineData("，")]
    [InlineData(", , ，")]
    public void MatchesGroupedWords(string separator)
    {
        foreach (var reverse in new[] { false, true })
        {
            var judge = Keywords(["任务完成" + separator + "全部成功"]);
            Assert.Null(judge.ObserveLine(judge.InputIdentity, reverse ? "任务完成" : "全部成功"));
            Assert.Equal(BusinessOutcome.Unverified, judge.Business);
            var decision = judge.ObserveLine(judge.InputIdentity, reverse ? "全部成功" : "任务完成");
            Assert.Equal(BusinessOutcome.Succeeded, Assert.IsType<BusinessJudgementDecision>(decision).Outcome);
        }
    }

    [Fact(DisplayName = "Alternative Groups Do Not Require The Words From Another Incomplete Group")]
    public void SeparatesAlternativeGroups()
    {
        var judge = Keywords(["first,missing", "alternative,done"]);
        Assert.Null(judge.ObserveLine(judge.InputIdentity, "first"));
        Assert.Null(judge.ObserveLine(judge.InputIdentity, "done"));
        Assert.Equal(BusinessOutcome.Succeeded, judge.ObserveLine(judge.InputIdentity, "alternative")!.Outcome);
    }

    [Theory(DisplayName = "Newlines Within An Expression Separate Alternative Groups")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void SplitsAlternativeLines(string newline)
    {
        var judge = Keywords(["  first,missing " + newline + " " + newline + " alternative  "]);
        Assert.Equal(BusinessOutcome.Succeeded, judge.ObserveLine(judge.InputIdentity, "ALTERNATIVE")!.Outcome);
    }

    [Fact(DisplayName = "Repeated Words Are Case Insensitive And Do Not Require Repeated Log Occurrences")]
    public void IgnoresWordCase()
    {
        var judge = Keywords([" DONE,done，Done "]);
        Assert.Equal(BusinessOutcome.Succeeded, judge.ObserveLine(judge.InputIdentity, "[info] DoNe")!.Outcome);
        Assert.Null(judge.ObserveLine(judge.InputIdentity, "done"));
    }

    [Theory(DisplayName = "Competing Terminal Groups On The Same Line Follow Their Text Positions")]
    [InlineData("success first then failure", BusinessOutcome.Succeeded)]
    [InlineData("failure first then success", BusinessOutcome.Failed)]
    public void OrdersTerminalMatches(string line, BusinessOutcome expected)
    {
        var judge = Keywords(["success"], ["failure"]);
        Assert.Equal(expected, judge.ObserveLine(judge.InputIdentity, line)!.Outcome);
    }

    [Fact(DisplayName = "A Group Completes At Its Last Required Words Position Rather Than Its First Words Position")]
    public void UsesCompletionPosition()
    {
        var judge = Keywords(["start,done"], ["failed"]);
        Assert.Equal(BusinessOutcome.Failed, judge.ObserveLine(judge.InputIdentity, "start failed done")!.Outcome);
    }

    [Fact(DisplayName = "A Previously Accumulated Group Competes At The Word That Completes It On The Current Line")]
    public void CombinesEarlierLines()
    {
        var judge = Keywords(["previous,done"], ["failed"]);
        Assert.Null(judge.ObserveLine(judge.InputIdentity, "previous"));
        Assert.Equal(BusinessOutcome.Succeeded, judge.ObserveLine(judge.InputIdentity, "done then failed")!.Outcome);
    }

    [Fact(DisplayName = "The Earliest Completed Alternative Group Determines The Comparison")]
    public void ChoosesEarliestGroup()
    {
        var judge = Keywords(["start,late", "early"], ["failed"]);
        Assert.Equal(BusinessOutcome.Succeeded, judge.ObserveLine(judge.InputIdentity, "start early failed late")!.Outcome);
    }

    [Fact(DisplayName = "Equal Terminal Positions Preserve The Existing Failure Tie Rule")]
    public void PrefersFailureTies()
    {
        var judge = Keywords(["done"], ["done"]);
        Assert.Equal(BusinessOutcome.Failed, judge.ObserveLine(judge.InputIdentity, "done")!.Outcome);
    }

    [Theory(DisplayName = "Later Keyword Events Cannot Replace The First Terminal Or Publish It Again")]
    [InlineData(true)]
    [InlineData(false)]
    public void KeepsFirstTerminal(bool failureFirst)
    {
        var judge = Keywords(["done"], ["failed"]);
        var first = judge.ObserveLine(judge.InputIdentity, failureFirst ? "failed" : "done");
        Assert.NotNull(first);
        Assert.Null(judge.ObserveLine(judge.InputIdentity, failureFirst ? "done" : "failed"));
        Assert.Null(judge.ObserveLine(judge.InputIdentity, "failed done"));
        Assert.Equal(first, judge.Decision);
        Assert.Equal(failureFirst ? BusinessOutcome.Failed : BusinessOutcome.Succeeded, judge.Business);
    }

    [Fact(DisplayName = "Another Attempt Cannot Contribute Even A Partial Keyword Group")]
    public void IsolatesAttemptWords()
    {
        var judge = Keywords(["first,last"]);
        Assert.Null(judge.ObserveLine(judge.InputIdentity with { AttemptId = Guid.NewGuid() }, "first"));
        Assert.Null(judge.ObserveLine(judge.InputIdentity, "last"));
        Assert.Equal(BusinessOutcome.Unverified, judge.Business);
        Assert.Equal(BusinessOutcome.Succeeded, judge.ObserveLine(judge.InputIdentity, "first")!.Outcome);
    }

    [Theory(DisplayName = "Explicit Script Language Ignores All Keyword Groups")]
    [InlineData(AutomationJudgementLanguage.JavaScript)]
    [InlineData(AutomationJudgementLanguage.Python)]
    public void HonorsScriptLanguage(AutomationJudgementLanguage language)
    {
        var judge = new AttemptBusinessJudgement(Guid.NewGuid(), new(language, ["done"], ["failed"], "source"));
        Assert.Equal(ExecutionJudgementMode.Script, judge.Mode);
        Assert.Null(judge.ObserveLine(judge.InputIdentity, "failed done"));
        Assert.Equal(BusinessOutcome.Unverified, judge.Business);
        var call = Assert.IsType<JudgementInvocation>(judge.TryBeginPeriodic());
        Assert.True(judge.Complete(call, new(BusinessOutcome.Partial, "two tasks remain")));
        Assert.Equal(BusinessOutcome.Partial, judge.Business);
    }

    [Theory(DisplayName = "Empty Script And Worker Fault Never Silently Fall Back To Keywords")]
    [InlineData(AutomationJudgementLanguage.JavaScript, null)]
    [InlineData(AutomationJudgementLanguage.JavaScript, "")]
    [InlineData(AutomationJudgementLanguage.Python, " ")]
    public void RejectsScriptFallback(AutomationJudgementLanguage language, string? source)
    {
        var judge = new AttemptBusinessJudgement(Guid.NewGuid(), new(language, ["done"], ["failed"], source));
        Assert.Equal(ExecutionJudgementMode.Script, judge.Mode);
        Assert.Null(judge.ObserveLine(judge.InputIdentity, "done"));
        judge.RequestFinal();
        Assert.True(judge.CompleteFault(Assert.IsType<JudgementInvocation>(judge.TryBeginFinal())));
        Assert.True(judge.FinalCompleted); Assert.Null(judge.Decision);
        Assert.Equal(BusinessOutcome.Unverified, judge.Business);
    }

    [Fact(DisplayName = "No Rules Leave Business Unverified And Do Not Create Script Work")]
    public void LeavesBusinessUnverified()
    {
        var judge = Keywords([]);
        Assert.Equal(ExecutionJudgementMode.None, judge.Mode);
        Assert.Null(judge.ObserveLine(judge.InputIdentity, "exit code 0 done success"));
        judge.RequestFinal();
        Assert.Null(judge.TryBeginPeriodic()); Assert.Null(judge.TryBeginFinal());
        Assert.Null(judge.Decision); Assert.Equal(BusinessOutcome.Unverified, judge.Business);
        Assert.False(judge.FinalCompleted);
    }

    [Fact(DisplayName = "Whitespace Groups Are Ignored And An Unused Script Does Not Override Explicit Keyword Language")]
    public void IgnoresWhitespaceGroups()
    {
        var judge = new AttemptBusinessJudgement(Guid.NewGuid(), new(AutomationJudgementLanguage.Keywords, [" ,， \r\n "], [], "unused source"));
        Assert.Equal(ExecutionJudgementMode.None, judge.Mode);
        Assert.Null(judge.ObserveLine(judge.InputIdentity, "anything"));
    }

    [Fact(DisplayName = "Cancellation Blocks Further Lines And Preserves An Already Accepted Business Fact")]
    public void PreservesCancelledFacts()
    {
        var accepted = Keywords(["done"], ["failed"]);
        var decision = accepted.ObserveLine(accepted.InputIdentity, "done");
        accepted.Cancel(); accepted.Cancel();
        Assert.Null(accepted.ObserveLine(accepted.InputIdentity, "failed"));
        Assert.Equal(decision, accepted.Decision); Assert.Equal(BusinessOutcome.Succeeded, accepted.Business);
        var pending = Keywords(["first,last"]);
        pending.ObserveLine(pending.InputIdentity, "first"); pending.Cancel();
        Assert.Null(pending.ObserveLine(pending.InputIdentity, "last"));
        Assert.Equal(BusinessOutcome.Unverified, pending.Business);
    }

    [Fact(DisplayName = "Judgement Only Supplies The Business Dimension Without Changing Engine Lifecycle Or Recovery Facts")]
    public void SeparatesBusinessDimension()
    {
        var judge = Keywords(["done"]);
        var facts = new AttemptFacts(Guid.NewGuid(), judge.AttemptId, 1, EngineOutcome.Failed,
            BusinessOutcome.Unverified, LifecycleOutcome.Interrupted, RecoveryOutcome.Quarantined, "writer_unknown");
        judge.ObserveLine(judge.InputIdentity, "done");
        var projected = facts with { Business = judge.Business };
        Assert.Equal(EngineOutcome.Failed, projected.Engine); Assert.Equal(LifecycleOutcome.Interrupted, projected.Lifecycle);
        Assert.Equal(RecoveryOutcome.Quarantined, projected.Recovery); Assert.Equal("writer_unknown", projected.ReasonCode);
        Assert.Equal(BusinessOutcome.Succeeded, projected.Business);
    }

    private static AttemptBusinessJudgement Keywords(System.Collections.Immutable.ImmutableArray<string> success,
        System.Collections.Immutable.ImmutableArray<string> failure = default) => new(Guid.NewGuid(),
        new(AutomationJudgementLanguage.Keywords, success, failure.IsDefault ? [] : failure, null));
}
