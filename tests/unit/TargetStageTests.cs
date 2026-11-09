using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class TargetStageTests
{
    [Theory(DisplayName = "A Failed Target Stage Closes Owned Pc Before Main Dispatch")]
    [InlineData(TargetStageOutcome.LaunchFailed)]
    [InlineData(TargetStageOutcome.ReadinessTimedOut)]
    public void ClosesFailedLaunches(TargetStageOutcome stage)
    {
        var primary = new ExecutionStepFact("binding", EngineOutcome.NotStarted, BusinessOutcome.Unverified, "execution_not_dispatched");
        var cleanup = TargetLifecyclePolicy.Cleanup(new(ExecutionTargetKind.Pc, true, false),
            ExecutionTargetAuthority.OwnedPcProcess, primary, ExecutionStopReason.Completed, ExecutionRunContinuation.Final,
            targetStage: stage);
        Assert.Equal(TargetCleanupAction.CloseOwnedPc, cleanup.Application);
    }

    [Theory(DisplayName = "Target Stage Failure Cannot Grant Ownership Over An External Ready Pc")]
    [InlineData(TargetStageOutcome.LaunchFailed)]
    [InlineData(TargetStageOutcome.ReadinessTimedOut)]
    public void PreservesExternalOwnership(TargetStageOutcome stage)
    {
        var primary = new ExecutionStepFact("binding", EngineOutcome.NotStarted, BusinessOutcome.Unverified, "execution_not_dispatched");
        var cleanup = TargetLifecyclePolicy.Cleanup(new(ExecutionTargetKind.Pc, true, true),
            ExecutionTargetAuthority.ExternalReadinessOnly, primary, ExecutionStopReason.Completed, ExecutionRunContinuation.Final,
            targetStage: stage);
        Assert.Equal(TargetCleanupAction.PreserveExternal, cleanup.Application);
    }

    [Theory(DisplayName = "Accepted Cancellation Keeps Its Frozen Cleanup Preference After An Earlier Target Stage Failure")]
    [InlineData(false, TargetCleanupAction.RetainOwned)]
    [InlineData(true, TargetCleanupAction.CloseOwnedPc)]
    public void KeepsFrozenCleanup(
        bool forceClose, TargetCleanupAction expected)
    {
        var primary = new ExecutionStepFact("binding", EngineOutcome.NotStarted, BusinessOutcome.Unverified, "execution_not_dispatched");
        Assert.Equal(expected, TargetLifecyclePolicy.Cleanup(new(ExecutionTargetKind.Pc, true, forceClose),
            ExecutionTargetAuthority.OwnedPcProcess, primary, ExecutionStopReason.Cancelled, ExecutionRunContinuation.Final,
            targetStage: TargetStageOutcome.ReadinessTimedOut).Application);
    }

    [Fact(DisplayName = "A Target Stage Failure Stops The Explicit Android Application And Keeps Its Device Running")]
    public void ClosesAndroidFailure()
    {
        var primary = new ExecutionStepFact("binding", EngineOutcome.NotStarted, BusinessOutcome.Unverified, "execution_not_dispatched");
        var cleanup = TargetLifecyclePolicy.Cleanup(new(ExecutionTargetKind.Android, true, false),
            ExecutionTargetAuthority.ExplicitAndroidControl, primary, ExecutionStopReason.Completed, ExecutionRunContinuation.Final,
            targetStage: TargetStageOutcome.LaunchFailed);
        Assert.Equal(TargetCleanupAction.StopAndroidApplication, cleanup.Application);
        Assert.Equal(TargetCleanupAction.None, cleanup.Device);
    }

    [Theory(DisplayName = "Target Failure Cannot Reclassify Already Started Or Verified Main Facts")]
    [InlineData(EngineOutcome.Succeeded, BusinessOutcome.Unverified)]
    [InlineData(EngineOutcome.NotStarted, BusinessOutcome.Succeeded)]
    public void KeepsStartedFacts(EngineOutcome engine, BusinessOutcome business)
    {
        Assert.Throws<ArgumentException>(() => TargetLifecyclePolicy.Cleanup(new(ExecutionTargetKind.Pc, true, false),
            ExecutionTargetAuthority.OwnedPcProcess, new("binding", engine, business, "actual_fact"),
            ExecutionStopReason.Completed, ExecutionRunContinuation.Final, targetStage: TargetStageOutcome.LaunchFailed));
    }

    [Fact(DisplayName = "Unknown Stage And Unconfigured Target Cannot Become Failure Authority")]
    public void RejectsUnknownStages()
    {
        var primary = new ExecutionStepFact("binding", EngineOutcome.NotStarted, BusinessOutcome.Unverified, "execution_not_dispatched");
        Assert.Throws<ArgumentException>(() => TargetLifecyclePolicy.Cleanup(new(ExecutionTargetKind.Pc, true, false),
            ExecutionTargetAuthority.OwnedPcProcess, primary, ExecutionStopReason.Completed, ExecutionRunContinuation.Final,
            targetStage: (TargetStageOutcome)99));
        Assert.Throws<ArgumentException>(() => TargetLifecyclePolicy.Cleanup(new(ExecutionTargetKind.None, false, false),
            ExecutionTargetAuthority.Unknown, primary, ExecutionStopReason.Completed, ExecutionRunContinuation.Final,
            targetStage: TargetStageOutcome.LaunchFailed));
    }
}
