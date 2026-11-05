using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class LauncherFactsTests
{
    [Theory(DisplayName = "A Launcher Launch Fact Preserves Main Business And The Main Reason Wins At Equal Severity")]
    [InlineData(EngineOutcome.Succeeded, EngineOutcome.Failed, EngineOutcome.Failed, false)]
    [InlineData(EngineOutcome.Failed, EngineOutcome.Failed, EngineOutcome.Failed, true)]
    [InlineData(EngineOutcome.Unknown, EngineOutcome.Succeeded, EngineOutcome.Unknown, true)]
    [InlineData(EngineOutcome.NotStarted, EngineOutcome.Unknown, EngineOutcome.Unknown, false)]
    [InlineData(EngineOutcome.Succeeded, EngineOutcome.Succeeded, EngineOutcome.Succeeded, true)]
    [InlineData(EngineOutcome.NotStarted, EngineOutcome.NotStarted, EngineOutcome.NotStarted, true)]
    public void PreservesMainBusiness(
        EngineOutcome main, EngineOutcome launcher, EngineOutcome expected, bool keepMainReason)
    {
        var primary = new ExecutionStepFact("binding-real", main, BusinessOutcome.Unverified, "primary_fact");
        var result = ProviderFactsPolicy.ComposeLauncher(primary, launcher, "launcher_fact");
        Assert.Equal(expected, result.Engine); Assert.Equal(primary.Business, result.Business);
        Assert.Equal(primary.StepId, result.StepId);
        Assert.Equal(keepMainReason ? "primary_fact" : "launcher_fact", result.ReasonCode);
    }

    [Theory(DisplayName = "An Unfinished Or Invented Launcher Outcome Cannot Become A Terminal Auxiliary Fact")]
    [InlineData(EngineOutcome.Running)]
    [InlineData(EngineOutcome.Cancelled)]
    [InlineData((EngineOutcome)99)]
    public void RejectsUnfinishedOutcomes(EngineOutcome launcher) =>
        Assert.Throws<ArgumentException>(() => ProviderFactsPolicy.ComposeLauncher(
            new("binding-real", EngineOutcome.NotStarted, BusinessOutcome.Unverified, "main_unstarted"), launcher, "launcher_fact"));
}
