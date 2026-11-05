namespace NexusPipeline.Core.Execution;

public enum ExecutionTargetKind { None, Pc, Android }
public enum ExecutionTargetReadiness { Unobserved, NotReady, Ready, Unknown }
public enum ExecutionTargetAuthority { Unknown, ExternalReadinessOnly, OwnedPcProcess, ExplicitAndroidControl }
public enum TargetLaunchAction { Skip, Observe, Start, UseReady, Suppress, Block }
public enum TargetCleanupAction { None, PreserveExternal, RetainOwned, CloseOwnedPc, StopAndroidApplication, ShutdownAndroid }
public enum TargetStageOutcome { NotAttempted, Ready, LaunchFailed, ReadinessTimedOut }
public enum ExecutionRunContinuation { Undecided, AuthorizedRetry, Final }

public sealed record ExecutionTargetPolicy(ExecutionTargetKind Kind, bool LaunchByHost, bool ForceCloseAtEnd);
public sealed record ExecutionTargetCleanup(TargetCleanupAction Application, TargetCleanupAction Device,
    bool RequiresFinalRunDecision, string ReasonCode);

public static class TargetLifecyclePolicy
{
    public static TargetLaunchAction Launch(ExecutionTargetPolicy policy, ExecutionTargetReadiness readiness,
        ExecutionTargetAuthority authority, ExecutionStopReason? stop)
    {
        Require(policy, authority);
        if (!Enum.IsDefined(readiness) || stop is not null && stop is not (ExecutionStopReason.Cancelled or
            ExecutionStopReason.Interrupted or ExecutionStopReason.TotalBudgetExhausted or ExecutionStopReason.TargetUnavailable))
            throw Invalid();
        if (stop is not null) return TargetLaunchAction.Suppress;
        if (policy.Kind == ExecutionTargetKind.None || !policy.LaunchByHost) return TargetLaunchAction.Skip;
        if (readiness == ExecutionTargetReadiness.Unknown) return TargetLaunchAction.Block;
        if (readiness == ExecutionTargetReadiness.Unobserved) return TargetLaunchAction.Observe;
        if (readiness == ExecutionTargetReadiness.Ready) return TargetLaunchAction.UseReady;
        return TargetLaunchAction.Start;
    }

    public static ExecutionTargetCleanup Cleanup(ExecutionTargetPolicy policy, ExecutionTargetAuthority authority,
        ExecutionStepFact main, ExecutionStopReason stop, ExecutionRunContinuation continuation,
        StepStopCause localStop = StepStopCause.Continue,
        TargetStageOutcome targetStage = TargetStageOutcome.NotAttempted)
    {
        Require(policy, authority); ArgumentNullException.ThrowIfNull(main);
        if (!Enum.IsDefined(main.Engine) || main.Engine == EngineOutcome.Running || !Enum.IsDefined(main.Business) ||
            !Enum.IsDefined(stop) || !Enum.IsDefined(continuation) || !Enum.IsDefined(localStop) || !Enum.IsDefined(targetStage) || string.IsNullOrWhiteSpace(main.StepId))
            throw Invalid();
        var targetFailed = targetStage is TargetStageOutcome.LaunchFailed or TargetStageOutcome.ReadinessTimedOut;
        if (targetFailed && (policy.Kind == ExecutionTargetKind.None || !policy.LaunchByHost ||
            main.Engine != EngineOutcome.NotStarted || main.Business != BusinessOutcome.Unverified ||
            localStop != StepStopCause.Continue)) throw Invalid();
        if (policy.Kind == ExecutionTargetKind.None)
            return new(TargetCleanupAction.None, TargetCleanupAction.None, false, "execution_target_not_configured");
        if (authority == ExecutionTargetAuthority.Unknown)
            throw new InvalidOperationException("execution_target_authority_unproven");
        if (authority == ExecutionTargetAuthority.ExternalReadinessOnly)
            return new(TargetCleanupAction.PreserveExternal, TargetCleanupAction.None, false, "execution_target_external_preserved");
        var cancelled = stop == ExecutionStopReason.Cancelled;
        var interrupted = stop is ExecutionStopReason.Interrupted or ExecutionStopReason.TotalBudgetExhausted or ExecutionStopReason.TargetUnavailable;
        var failure = !cancelled && !interrupted && (targetFailed || main.Engine == EngineOutcome.Failed || main.Business == BusinessOutcome.Failed ||
            localStop is StepStopCause.BusinessFailed or StepStopCause.StallTimeout or StepStopCause.StepTotalTimeout);
        var closeApplication = failure || policy.ForceCloseAtEnd &&
            (cancelled || !interrupted || continuation == ExecutionRunContinuation.Final);
        if (policy.Kind == ExecutionTargetKind.Pc)
            return new(closeApplication ? TargetCleanupAction.CloseOwnedPc : TargetCleanupAction.RetainOwned,
                TargetCleanupAction.None, false, closeApplication ? "execution_target_close_owned" : "execution_target_retain_owned");
        var application = failure || cancelled && policy.ForceCloseAtEnd
            ? TargetCleanupAction.StopAndroidApplication : TargetCleanupAction.None;
        var pendingFinal = policy.ForceCloseAtEnd && continuation == ExecutionRunContinuation.Undecided;
        var device = policy.ForceCloseAtEnd && continuation == ExecutionRunContinuation.Final
            ? TargetCleanupAction.ShutdownAndroid : TargetCleanupAction.None;
        return new(application, device, pendingFinal, pendingFinal ? "execution_target_finality_pending" :
            device == TargetCleanupAction.ShutdownAndroid ? "execution_target_shutdown_requested" : "execution_target_cleanup_decided");
    }

    private static void Require(ExecutionTargetPolicy policy, ExecutionTargetAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!Enum.IsDefined(policy.Kind) || !Enum.IsDefined(authority) ||
            policy.Kind == ExecutionTargetKind.None && (policy.LaunchByHost || policy.ForceCloseAtEnd) ||
            authority == ExecutionTargetAuthority.OwnedPcProcess && policy.Kind != ExecutionTargetKind.Pc ||
            authority == ExecutionTargetAuthority.ExplicitAndroidControl && policy.Kind != ExecutionTargetKind.Android)
            throw Invalid();
    }

    private static ArgumentException Invalid() => new("execution_target_policy_invalid");
}
