using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class QueueDependencyTests
{
    [Theory(DisplayName = "Real Nonzero Workers Release A Dependent Only After Proven Business Or None Mode Success")]
    [InlineData("success", true)]
    [InlineData("none", true)]
    [InlineData("failed", false)]
    [InlineData("unverified", false)]
    public async Task RunsProvenDependencies(string judgement, bool shouldRun)
    {
        var policy = judgement switch
        {
            "success" => new AutomationJudgement(AutomationJudgementLanguage.Keywords, ["ready"], [], null),
            "failed" => new(AutomationJudgementLanguage.Keywords, [], ["ready"], null),
            "unverified" => new(AutomationJudgementLanguage.Keywords, ["absent"], [], null),
            _ => new(AutomationJudgementLanguage.Keywords, [], [], null)
        };
        using var fixture = new ExecutionRuntimeFixture(judgement: policy, independentSuccessors: true, successDependency: true);
        var receipt = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync().WaitAsync(TimeSpan.FromSeconds(30)));
        var state = fixture.State;
        var predecessor = StepResultCodec.Read(state.Latest.Root, state.Total).First();
        if (shouldRun)
        {
            Assert.Equal(EngineOutcome.Failed, predecessor.Latest.Engine);
            Assert.Equal(judgement == "none" ? BusinessOutcome.NotApplicable : BusinessOutcome.Succeeded, predecessor.Latest.Business);
        }
        Assert.Equal(LifecycleOutcome.Completed, receipt.Facts.Lifecycle);
        Assert.Equal(shouldRun, state.Jobs.Any(job => job.StepId == state.Total.Steps[1].StepId));
        Assert.Equal(shouldRun ? 3 : 2, fixture.Base.Count("journal_writer_job_roots"));
        Assert.Equal(shouldRun ? 0 : 1, BindingSkipCodec.Read(state.Latest.Root, state.Total).Length);
        if (!shouldRun)
            Assert.Equal("execution_queue_dependency_not_successful", Assert.Single(BindingSkipCodec.Read(state.Latest.Root, state.Total)).ReasonCode);
        Assert.True(fixture.Base.Index.Current.IsReady);
    }

    [Fact(DisplayName = "A Missing Collected Predecessor Takes Priority Over An Exhausted Daily Quota")]
    public void RejectsMissingFacts()
    {
        using var fixture = new ExecutionRuntimeFixture(independentSuccessors: true, successDependency: true, configure: source =>
        {
            var catalog = source.Source.Definitions.Read();
            var dependent = catalog.Bindings.Single(binding => binding.ScriptId == catalog.Queues.Single().ScriptIds[1]);
            var changes = new AutomationChanges(catalog.Revision, [], [], [], [dependent with { MaximumSuccessfulRunsPerDay = 1 }], [], [], []);
            source.Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "dependency-quota-fixture", changes), changes, DateTimeOffset.UtcNow);
        });
        var state = fixture.State;
        Assert.Empty(StepResultCodec.Read(state.Latest.Root, state.Total));
        Assert.Equal("execution_queue_dependency_not_successful", BindingSkipStore.Reason(state, state.Total.Steps[1].StepId, 1));
        Assert.Empty(state.Jobs);
    }

    [Fact(DisplayName = "An Actual Prior Success Still Applies The Independent Daily Limit On The Next Queue Start")]
    public async Task PreservesDailyLimit()
    {
        using var fixture = new ExecutionRuntimeFixture(judgement: new(AutomationJudgementLanguage.Keywords, ["ready"], [], null),
            independentSuccessors: true, configure: source =>
        {
            var catalog = source.Source.Definitions.Read();
            var first = catalog.Bindings.Single(binding => binding.IsEnabled && binding.ScriptId == catalog.Queues.Single().ScriptIds[0]);
            var changes = new AutomationChanges(catalog.Revision, [], [], [], [first with { MaximumSuccessfulRunsPerDay = 1 }], [], [], []);
            source.Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "daily-quota-fixture", changes), changes, DateTimeOffset.UtcNow);
        });
        Assert.NotNull(await fixture.Consumer.RunNextAsync().WaitAsync(TimeSpan.FromSeconds(30)));
        var firstStep = fixture.State.Total.Steps[0];
        Assert.Equal(1, ExecutionBindingSuccesses.Today(fixture.Source.Database, DateTimeOffset.UtcNow)[Guid.Parse(firstStep.BindingId!)]);
        var accepted = fixture.Base.Service.Start(Guid.NewGuid(), "daily-quota-fixture",
            fixture.Source.Request(new QueueTarget(fixture.Source.QueueId)), DateTimeOffset.UtcNow);
        var next = Assert.IsType<AttemptReceipt>(await fixture.Consumer.RunNextAsync().WaitAsync(TimeSpan.FromSeconds(30)));
        var state = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), accepted.JournalId);
        Assert.Equal(LifecycleOutcome.Completed, next.Facts.Lifecycle);
        var skipped = Assert.Single(state.Total.Selection, item => item.Disposition == "skipped");
        Assert.Equal(firstStep.BindingId, skipped.BindingId);
        Assert.Equal("execution_daily_success_limit", skipped.ReasonCode);
        Assert.Equal(2, state.Steps.Length);
        Assert.Equal(5, fixture.Base.Count("journal_writer_job_roots"));
        Assert.Empty(BindingSkipCodec.Read(state.Latest.Root, state.Total));
    }

    [Fact(DisplayName = "A Real Success Grace Stop Releases Its Dependent After The Original Sixty Second Grace")]
    public async Task ReleasesGraceDependency()
    {
        using var fixture = new ExecutionRuntimeFixture(mode: "wait", limits: new(1, 0, 0),
            judgement: new(AutomationJudgementLanguage.Keywords, ["ready"], [], null), independentSuccessors: true, successDependency: true);
        Assert.NotNull(await fixture.Consumer.RunNextAsync().WaitAsync(TimeSpan.FromSeconds(90)));
        var state = fixture.State;
        var first = StepResultCodec.Read(state.Latest.Root, state.Total).First();
        Assert.Equal(BusinessOutcome.Succeeded, first.Latest.Business);
        Assert.Equal("execution_success_grace_expired", first.Latest.ReasonCode);
        Assert.Contains(state.Jobs, job => job.StepId == state.Total.Steps[1].StepId);
        Assert.Empty(BindingSkipCodec.Read(state.Latest.Root, state.Total));
        Assert.Equal(3, fixture.Base.Count("journal_writer_job_roots"));
    }
}
