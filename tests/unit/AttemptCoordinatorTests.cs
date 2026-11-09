using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class AttemptCoordinatorTests
{
    [Fact(DisplayName = "Every Step Is Collected And Its Configuration Restored Before The Next Preparation And Final Publication")]
    public async Task OrdersStepCompletion()
    {
        var session = new Session(); var result = await new ExecutionAttemptCoordinator().RunAsync(session);
        Assert.Equal(session.Receipt, result);
        Assert.Equal(new[] { "groups", "prepare:a", "enter:a", "execute:a", "collect:a", "restore:a", "prepare:b", "enter:b", "execute:b",
            "collect:b", "restore:b", "freeze:Completed", "collectRemaining", "closeSuffix", "applied", "restoreRemaining", "commit:Completed" }, session.Trace);
    }

    [Theory(DisplayName = "Manual Cancellation Before Effect Entry Collects Only Actual Preparation And Never Invents An Entered Step")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollectsCancelledPreparation(bool afterPreparation)
    {
        var session = new Session { CancelBeforePreparation = !afterPreparation, CancelAfterPreparation = afterPreparation };
        await new ExecutionAttemptCoordinator().RunAsync(session);
        Assert.False(session.HasEnteredEffect); Assert.DoesNotContain(session.Trace, item => item.StartsWith("enter:", StringComparison.Ordinal));
        Assert.DoesNotContain(session.Trace, item => item.StartsWith("execute:", StringComparison.Ordinal));
        Assert.DoesNotContain("prepare:b", session.Trace); Assert.DoesNotContain("applied", session.Trace);
        Assert.Equal(afterPreparation ? 1 : 0, session.Trace.Count(item => item.StartsWith("prepare:", StringComparison.Ordinal)));
        Assert.Equal(new[] { "freeze:Cancelled", "collectRemaining", "closeSuffix", "restoreRemaining", "commit:Cancelled" }, session.Trace.TakeLast(5));
    }

    [Fact(DisplayName = "Host Shutdown During A Started Step Freezes Before Collection And Never Dispatches The Successor")]
    public async Task FreezesShutdownDispatch()
    {
        using var stopping = new CancellationTokenSource();
        var session = new Session { StopHostDuringExecution = stopping };
        await new ExecutionAttemptCoordinator().RunAsync(session, stopping.Token);
        Assert.DoesNotContain("prepare:b", session.Trace);
        Assert.Equal(new[] { "freeze:Interrupted", "collectRemaining", "closeSuffix", "applied", "restoreRemaining", "commit:Interrupted" }, session.Trace.TakeLast(6));
    }

    [Fact(DisplayName = "Failing Execution Uses The Recovery Port And Cannot Publish A Completed Receipt Or Dispatch Another Step")]
    public async Task RecoversFailedExecution()
    {
        var session = new Session { ExecutionFailure = new IOException("fixture_execution_failed") };
        var error = await Assert.ThrowsAsync<IOException>(() => new ExecutionAttemptCoordinator().RunAsync(session));
        Assert.Equal("fixture_execution_failed", error.Message); Assert.Equal("quarantine", session.Trace.Last());
        Assert.DoesNotContain("prepare:b", session.Trace); Assert.DoesNotContain(session.Trace, item => item.StartsWith("commit:", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Failed Restoration Cannot Release The Next Step Or Become A Completed Attempt")]
    public async Task BlocksFailedRestoration()
    {
        var session = new Session { FailRestoration = true };
        await Assert.ThrowsAsync<IOException>(() => new ExecutionAttemptCoordinator().RunAsync(session));
        Assert.Contains("collect:a", session.Trace); Assert.Contains("restore:a", session.Trace); Assert.Equal("quarantine", session.Trace.Last());
        Assert.DoesNotContain("prepare:b", session.Trace); Assert.DoesNotContain(session.Trace, item => item.StartsWith("commit:", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "An Exhausted Shared Budget Stops Before Any Successor Preparation And Preserves The Collected First Step")]
    public async Task StopsExhaustedBudget()
    {
        var session = new Session { ExhaustAfterFirstRestore = true };
        await new ExecutionAttemptCoordinator().RunAsync(session);
        Assert.Contains("restore:a", session.Trace); Assert.DoesNotContain("prepare:b", session.Trace);
        Assert.Contains("freeze:TotalBudgetExhausted", session.Trace); Assert.Equal("commit:TotalBudgetExhausted", session.Trace.Last());
    }

    [Fact(DisplayName = "Unverified Target Readiness Freezes Dispatch Before Collection And Preserves Its Distinct Stop Cause")]
    public async Task PreservesTargetFailure()
    {
        var session = new Session { ExecutionStop = ExecutionStopReason.TargetUnavailable };
        await new ExecutionAttemptCoordinator().RunAsync(session);
        Assert.DoesNotContain("prepare:b", session.Trace);
        Assert.Equal(new[] { "freeze:TargetUnavailable", "collectRemaining", "closeSuffix", "applied", "restoreRemaining", "commit:TargetUnavailable" },
            session.Trace.TakeLast(6));
    }

    [Fact(DisplayName = "A Session Cannot Replace The Frozen Step Order While Preparation Is Running")]
    public async Task FreezesStepOrder()
    {
        var session = new Session { MutateStepList = true };
        await new ExecutionAttemptCoordinator().RunAsync(session);
        Assert.Empty(session.StepIds); Assert.Equal(new[] { "prepare:a", "prepare:b" }, session.Trace.Where(item => item.StartsWith("prepare:", StringComparison.Ordinal)));
    }

    [Fact(DisplayName = "An Invalid Stop Signal Cannot Be Used To Skip Runnable Steps Or Claim Completion")]
    public async Task RejectsInvalidStops()
    {
        var session = new Session { InvalidStop = true };
        Assert.Equal("execution_stop_request_invalid", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ExecutionAttemptCoordinator().RunAsync(session))).Message);
        Assert.Equal(new[] { "groups", "quarantine" }, session.Trace);
    }

    private sealed class Session : IExecutionAttemptSession
    {
        private readonly List<string> _stepIds = ["a", "b"];
        private bool _prepared, _restored;
        internal List<string> Trace { get; } = [];
        internal bool CancelBeforePreparation { get; init; }
        internal bool CancelAfterPreparation { get; init; }
        internal bool ExhaustAfterFirstRestore { get; init; }
        internal bool FailRestoration { get; init; }
        internal bool MutateStepList { get; init; }
        internal bool InvalidStop { get; init; }
        internal ExecutionStopReason? ExecutionStop { get; init; }
        internal CancellationTokenSource? StopHostDuringExecution { get; init; }
        internal IOException? ExecutionFailure { get; init; }
        internal AttemptReceipt Receipt { get; } = new(Guid.NewGuid(), Guid.NewGuid(), new('a', 64), new('b', 64),
            new(Guid.NewGuid(), Guid.NewGuid(), 1, EngineOutcome.Unknown, BusinessOutcome.Unverified,
                LifecycleOutcome.Completed, RecoveryOutcome.Restored, "unit_session_receipt"));
        public IReadOnlyList<string> StepIds => _stepIds;
        public bool HasEnteredEffect { get; private set; }
        public void PrepareConfigurationGroups() { Trace.Add("groups"); if (MutateStepList) _stepIds.Clear(); }
        public ExecutionStopReason? ReadStopRequest() => InvalidStop ? ExecutionStopReason.Completed :
            CancelBeforePreparation || CancelAfterPreparation && _prepared ? ExecutionStopReason.Cancelled :
            ExhaustAfterFirstRestore && _restored ? ExecutionStopReason.TotalBudgetExhausted : null;
        public void PrepareNextStep(string stepId) { _prepared = true; Trace.Add("prepare:" + stepId); }
        public void EnterEffect(string stepId) { HasEnteredEffect = true; Trace.Add("enter:" + stepId); }
        public Task<ExecutionStopReason?> ExecuteStepAsync(string stepId, CancellationToken hostStopping)
        {
            Trace.Add("execute:" + stepId);
            if (ExecutionFailure is not null) throw ExecutionFailure;
            if (StopHostDuringExecution is not null) { StopHostDuringExecution.Cancel(); hostStopping.ThrowIfCancellationRequested(); }
            return Task.FromResult(ExecutionStop);
        }
        public Task CollectStepAsync(string stepId) { Trace.Add("collect:" + stepId); return Task.CompletedTask; }
        public Task RestoreStepAsync(string stepId)
        {
            Trace.Add("restore:" + stepId); _restored = true;
            if (FailRestoration) throw new IOException("fixture_restore_failed");
            return Task.CompletedTask;
        }
        public void FreezeDispatch(ExecutionStopReason reason) => Trace.Add("freeze:" + reason);
        public Task CollectRemainingAsync() { Trace.Add("collectRemaining"); return Task.CompletedTask; }
        public void CloseUndispatched() => Trace.Add("closeSuffix");
        public void MarkApplied() => Trace.Add("applied");
        public Task RestoreRemainingAsync(ExecutionStopReason reason) { Trace.Add("restoreRemaining"); return Task.CompletedTask; }
        public AttemptReceipt Commit(ExecutionStopReason reason) { Trace.Add("commit:" + reason); return Receipt; }
        public Task QuarantineFailureAsync() { Trace.Add("quarantine"); return Task.CompletedTask; }
    }
}
