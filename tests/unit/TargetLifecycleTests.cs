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
        Assert.Equal(BusinessOutcome.Failed, facts.Business);
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
        Assert.Equal(expected, cleanup.Application); Assert.Equal(business, facts.Business);
    }

    [Theory(DisplayName = "Cancellation Controls Cleanup But Preserves An Earlier Failed Business Fact")]
    [InlineData(false, TargetCleanupAction.RetainOwned)]
    [InlineData(true, TargetCleanupAction.CloseOwnedPc)]
    public void PreservesCancelledBusiness(bool forceClose, TargetCleanupAction expected)
    {
        var facts = PrimaryFacts(EngineOutcome.Succeeded, BusinessOutcome.Failed);
        Assert.Equal(expected, TargetLifecyclePolicy.Cleanup(new(ExecutionTargetKind.Pc, true, forceClose),
            ExecutionTargetAuthority.OwnedPcProcess, facts, ExecutionStopReason.Cancelled, ExecutionRunContinuation.Final).Application);
        Assert.Equal(BusinessOutcome.Failed, facts.Business);
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

    [Theory(DisplayName = "A Proved Local Limit Closes The Owned Target Without Inventing Business Failure")]
    [InlineData(StepStopCause.StallTimeout)]
    [InlineData(StepStopCause.StepTotalTimeout)]
    public void ClosesLimitedTargets(StepStopCause cause)
    {
        var facts = PrimaryFacts(EngineOutcome.Succeeded, BusinessOutcome.Unverified);
        var cleanup = TargetLifecyclePolicy.Cleanup(new(ExecutionTargetKind.Pc, true, false),
            ExecutionTargetAuthority.OwnedPcProcess, facts, ExecutionStopReason.Completed, ExecutionRunContinuation.Final, cause);
        Assert.Equal(TargetCleanupAction.CloseOwnedPc, cleanup.Application);
        Assert.Equal(BusinessOutcome.Unverified, facts.Business);
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
