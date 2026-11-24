using NexusPipeline.Core.Execution;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class TargetLifecycleTests
{
    [Theory(DisplayName = "Launch Uses Only An Explicit Readiness Observation")]
    [InlineData(ExecutionTargetReadiness.Unobserved, TargetLaunchAction.Observe)]
    [InlineData(ExecutionTargetReadiness.NotReady, TargetLaunchAction.Start)]
    [InlineData(ExecutionTargetReadiness.Ready, TargetLaunchAction.UseReady)]
    [InlineData(ExecutionTargetReadiness.Unknown, TargetLaunchAction.Block)]
    public void RequiresReadinessObservation(
        ExecutionTargetReadiness readiness, TargetLaunchAction expected)
    {
        var policy = new ExecutionTargetPolicy(ExecutionTargetKind.Pc, true, false);
        Assert.Equal(expected, TargetLifecyclePolicy.Launch(policy, readiness, ExecutionTargetAuthority.Unknown, null));
    }

    [Theory(DisplayName = "An Accepted Stop Always Suppresses New Target Effects")]
    [InlineData(ExecutionStopReason.Cancelled)]
    [InlineData(ExecutionStopReason.Interrupted)]
    [InlineData(ExecutionStopReason.TotalBudgetExhausted)]
    [InlineData(ExecutionStopReason.TargetUnavailable)]
    public void SuppressesStoppedEffects(ExecutionStopReason stop)
    {
        Assert.Equal(TargetLaunchAction.Suppress, TargetLifecyclePolicy.Launch(
            new(ExecutionTargetKind.Pc, true, true), ExecutionTargetReadiness.NotReady, ExecutionTargetAuthority.Unknown, stop));
    }

    [Theory(DisplayName = "A Ready External Pc Target Is Not Launched Again And Force Close Does Not Grant Ownership")]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesExternalOwnership(bool forceClose)
    {
        var policy = new ExecutionTargetPolicy(ExecutionTargetKind.Pc, true, forceClose);
        Assert.Equal(TargetLaunchAction.UseReady, TargetLifecyclePolicy.Launch(policy,
            ExecutionTargetReadiness.Ready, ExecutionTargetAuthority.ExternalReadinessOnly, null));
        var facts = PrimaryFacts(EngineOutcome.Failed, BusinessOutcome.Failed);
        var cleanup = TargetLifecyclePolicy.Cleanup(policy, ExecutionTargetAuthority.ExternalReadinessOnly,
            facts, ExecutionStopReason.Completed, ExecutionRunContinuation.Final);
        Assert.Equal(TargetCleanupAction.PreserveExternal, cleanup.Application);
        Assert.Equal(TargetCleanupAction.None, cleanup.Device);
    }

    [Theory(DisplayName = "Pc Cleanup Uses Actual Facts And The Frozen Force Close Preference")]
    [InlineData(EngineOutcome.Succeeded, BusinessOutcome.Succeeded, false, TargetCleanupAction.RetainOwned)]
    [InlineData(EngineOutcome.Succeeded, BusinessOutcome.Partial, false, TargetCleanupAction.RetainOwned)]
    [InlineData(EngineOutcome.Failed, BusinessOutcome.Unverified, false, TargetCleanupAction.CloseOwnedPc)]
    [InlineData(EngineOutcome.Succeeded, BusinessOutcome.Failed, false, TargetCleanupAction.CloseOwnedPc)]
    [InlineData(EngineOutcome.Succeeded, BusinessOutcome.Succeeded, true, TargetCleanupAction.CloseOwnedPc)]
    public void UsesFrozenCleanup(EngineOutcome engine, BusinessOutcome business,
        bool forceClose, TargetCleanupAction expected)
    {
        var facts = PrimaryFacts(engine, business);
        var cleanup = TargetLifecyclePolicy.Cleanup(new(ExecutionTargetKind.Pc, true, forceClose),
            ExecutionTargetAuthority.OwnedPcProcess, facts, ExecutionStopReason.Completed, ExecutionRunContinuation.Final);
        Assert.Equal(expected, cleanup.Application);
    }

    [Theory(DisplayName = "Cancellation Uses The Frozen Force Close Preference")]
    [InlineData(false, TargetCleanupAction.RetainOwned)]
    [InlineData(true, TargetCleanupAction.CloseOwnedPc)]
    public void UsesCancelledCleanup(bool forceClose, TargetCleanupAction expected)
    {
        var facts = PrimaryFacts(EngineOutcome.Succeeded, BusinessOutcome.Failed);
        Assert.Equal(expected, TargetLifecyclePolicy.Cleanup(new(ExecutionTargetKind.Pc, true, forceClose),
            ExecutionTargetAuthority.OwnedPcProcess, facts, ExecutionStopReason.Cancelled, ExecutionRunContinuation.Final).Application);
    }

    [Theory(DisplayName = "Android Shutdown Uses Actual Run Finality Rather Than Maximum Attempt Number")]
    [InlineData(ExecutionRunContinuation.Undecided, TargetCleanupAction.None, true)]
    [InlineData(ExecutionRunContinuation.AuthorizedRetry, TargetCleanupAction.None, false)]
    [InlineData(ExecutionRunContinuation.Final, TargetCleanupAction.ShutdownAndroid, false)]
    public void UsesRunFinality(
        ExecutionRunContinuation continuation, TargetCleanupAction device, bool pending)
    {
        var cleanup = TargetLifecyclePolicy.Cleanup(new(ExecutionTargetKind.Android, true, true),
            ExecutionTargetAuthority.ExplicitAndroidControl, PrimaryFacts(EngineOutcome.Succeeded, BusinessOutcome.Succeeded),
            ExecutionStopReason.Completed, continuation);
        Assert.Equal(TargetCleanupAction.None, cleanup.Application);
        Assert.Equal(device, cleanup.Device); Assert.Equal(pending, cleanup.RequiresFinalRunDecision);
    }

    [Theory(DisplayName = "Android Application Cleanup Is Separate From Windows Process Ownership")]
    [InlineData(ExecutionStopReason.Completed, false, TargetCleanupAction.StopAndroidApplication)]
    [InlineData(ExecutionStopReason.Cancelled, false, TargetCleanupAction.None)]
    [InlineData(ExecutionStopReason.Cancelled, true, TargetCleanupAction.StopAndroidApplication)]
    public void SeparatesAndroidOwnership(ExecutionStopReason stop, bool forceClose,
        TargetCleanupAction application)
    {
        var cleanup = TargetLifecyclePolicy.Cleanup(new(ExecutionTargetKind.Android, true, forceClose),
            ExecutionTargetAuthority.ExplicitAndroidControl, PrimaryFacts(EngineOutcome.Succeeded, BusinessOutcome.Failed),
            stop, ExecutionRunContinuation.AuthorizedRetry);
        Assert.Equal(application, cleanup.Application); Assert.Equal(TargetCleanupAction.None, cleanup.Device);
    }

    [Theory(DisplayName = "A Proved Local Limit Closes The Owned Target")]
    [InlineData(StepStopCause.StallTimeout)]
    [InlineData(StepStopCause.StepTotalTimeout)]
    public void ClosesLimitedTargets(StepStopCause cause)
    {
        var facts = PrimaryFacts(EngineOutcome.Succeeded, BusinessOutcome.Unverified);
        var cleanup = TargetLifecyclePolicy.Cleanup(new(ExecutionTargetKind.Pc, true, false),
            ExecutionTargetAuthority.OwnedPcProcess, facts, ExecutionStopReason.Completed, ExecutionRunContinuation.Final, cause);
        Assert.Equal(TargetCleanupAction.CloseOwnedPc, cleanup.Application);
    }

    [Fact(DisplayName = "Unproved Ownership Unknown Enums And Cross Kind Authority Cannot Authorize Cleanup")]
    public void RejectsUnknownAuthority()
    {
        var facts = PrimaryFacts(EngineOutcome.Succeeded, BusinessOutcome.Succeeded);
        Assert.Throws<InvalidOperationException>(() => TargetLifecyclePolicy.Cleanup(new(ExecutionTargetKind.Pc, true, false),
            ExecutionTargetAuthority.Unknown, facts, ExecutionStopReason.Completed, ExecutionRunContinuation.Final));
        Assert.Throws<ArgumentException>(() => TargetLifecyclePolicy.Cleanup(new(ExecutionTargetKind.Pc, true, false),
            ExecutionTargetAuthority.ExplicitAndroidControl, facts, ExecutionStopReason.Completed, ExecutionRunContinuation.Final));
        Assert.Throws<ArgumentException>(() => TargetLifecyclePolicy.Launch(new((ExecutionTargetKind)99, true, false),
            ExecutionTargetReadiness.Ready, ExecutionTargetAuthority.Unknown, null));
        Assert.Throws<ArgumentException>(() => TargetLifecyclePolicy.Launch(new(ExecutionTargetKind.None, true, false),
            ExecutionTargetReadiness.Ready, ExecutionTargetAuthority.Unknown, null));
        Assert.Throws<ArgumentException>(() => TargetLifecyclePolicy.Launch(new(ExecutionTargetKind.Pc, true, false),
            ExecutionTargetReadiness.Ready, ExecutionTargetAuthority.Unknown, ExecutionStopReason.Completed));
    }

    private static ExecutionStepFact PrimaryFacts(EngineOutcome engine, BusinessOutcome business) =>
        new("binding", engine, business, "execution_main_observed");
}

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
