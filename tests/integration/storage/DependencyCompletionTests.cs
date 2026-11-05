using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class DependencyCompletionTests
{
    [Theory(DisplayName = "Preparation And Runtime Dependency Skips Never Register A Completion Candidate")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsDependencySkips(bool beforeStart)
    {
        await using var fixture = new CompletionActionFixture(successDependency: true, skipPredecessor: beforeStart,
            clock: new CompletionControlledClock());
        var receipt = await fixture.RunToReceipt();
        var state = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Accepted.JournalId);
        if (beforeStart)
        {
            Assert.Contains(state.Total.Selection, item => item.Disposition == "skipped" &&
                item.ReasonCode == "execution_queue_dependency_not_successful");
            Assert.Empty(BindingSkipCodec.Read(state.Latest.Root, state.Total));
        }
        else
        {
            Assert.DoesNotContain(state.Total.Selection, item => item.Disposition == "skipped");
            Assert.Equal("execution_queue_dependency_not_successful", Assert.Single(BindingSkipCodec.Read(state.Latest.Root, state.Total)).ReasonCode);
        }
        var finality = new ExecutionRunFinality(fixture.Source.Instance, fixture.Source.Database, new());
        Assert.Null(CompletionIntentSource.ReadSaved(fixture.Source.Instance, fixture.Source.Database, new(), finality, receipt));
        fixture.Completion.OnCommitted(receipt);
        CompletionActionStartup.VerifyReadOnly(fixture.Source.Instance, fixture.Source.Database, new());
        Assert.Equal(0, fixture.Base.Count("completion_candidates"));
        Assert.Null(fixture.Completion.PendingOperationId);
        Assert.Null(fixture.Completion.FailureCode);
        Assert.Equal(0, fixture.Power.Calls);
        Assert.Equal(0, fixture.Exit.Requests);
        Assert.True(fixture.Base.Index.Current.IsReady);
    }

    [Theory(DisplayName = "An Independent Failed Run Retains Completion Eligibility With Or Without An Ordinary Skipped Binding")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesIndependentFailure(bool skipPredecessor)
    {
        await using var fixture = new CompletionActionFixture(clock: new CompletionControlledClock(), skipPredecessor: skipPredecessor);
        var (receipt, countdown) = await fixture.RunToCountdown();
        Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine);
        var state = AttemptWriterState.Read(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Accepted.JournalId);
        Assert.Equal(skipPredecessor ? 1 : 0, state.Total.Selection.Count(item => item.Disposition == "skipped"));
        Assert.DoesNotContain(state.Total.Selection, item => item.ReasonCode == "execution_queue_dependency_not_successful");
        Assert.Equal(1, fixture.Base.Count("completion_candidates"));
        Assert.NotNull(CompletionIntentSource.ReadSaved(fixture.Source.Instance, fixture.Source.Database, new(),
            new ExecutionRunFinality(fixture.Source.Instance, fixture.Source.Database, new()), receipt));
        fixture.Completion.OnCommitted(receipt);
        Assert.Equal(1, fixture.Base.Count("completion_candidates"));
        CompletionActionStartup.VerifyReadOnly(fixture.Source.Instance, fixture.Source.Database, new());
        await fixture.Completion.CancelLocalAsync(countdown.OperationId, default);
        await fixture.WaitFor(CompletionEffectPhase.Cancelled, countdown.OperationId);
        Assert.Equal(0, fixture.Power.Calls);
        Assert.Equal(0, fixture.Exit.Requests);
    }
}
