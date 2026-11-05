using System.Collections.Immutable;

namespace NexusPipeline.Core.Execution;

public enum TargetExecutionOutcome
{
    Skipped, ExternalReady, OwnedReady, LaunchFailed, ReadinessTimedOut, Blocked, ReadinessUnknown,
    UnverifiedDispatch, StoppedByRequest, ControlledReady
}
public sealed record TargetExecutionFact(TargetExecutionOutcome Outcome, string ReasonCode);
public sealed record ExecutionTargetComposition(ExecutionStepFact Primary, TargetExecutionFact Target, ExecutionStepFact Combined)
{
    public ImmutableArray<TargetCommandFact> Commands { get; init; } = [];
    public TargetCleanupFact Cleanup { get; init; } =
        new(TargetCleanupOutcome.NotRequired, "execution_target_cleanup_not_required");
}

public static class TargetFactsPolicy
{
    public static ExecutionTargetComposition Compose(ExecutionStepFact primary, TargetExecutionFact target,
        IReadOnlyList<TargetCommandFact>? commands = null, TargetCleanupFact? cleanup = null)
    {
        ArgumentNullException.ThrowIfNull(primary); ArgumentNullException.ThrowIfNull(target);
        cleanup ??= new(TargetCleanupOutcome.NotRequired, "execution_target_cleanup_not_required");
        if (!Enum.IsDefined(target.Outcome) || !Enum.IsDefined(primary.Engine) || !Enum.IsDefined(primary.Business) ||
            primary.Engine == EngineOutcome.Running || string.IsNullOrWhiteSpace(primary.StepId) ||
            !Reason(primary.ReasonCode) || !Reason(target.ReasonCode) || !Enum.IsDefined(cleanup.Outcome) || !Reason(cleanup.ReasonCode))
            throw Invalid();
        if ((target.Outcome is TargetExecutionOutcome.LaunchFailed or TargetExecutionOutcome.ReadinessTimedOut or
                TargetExecutionOutcome.Blocked or TargetExecutionOutcome.ReadinessUnknown or TargetExecutionOutcome.StoppedByRequest) &&
            (primary.Engine != EngineOutcome.NotStarted || primary.Business != BusinessOutcome.Unverified))
            throw new ArgumentException("execution_target_primary_was_dispatched");
        var frozen = commands?.Select(value => value is null ? throw Invalid() : value with { }).ToImmutableArray() ?? [];
        if (frozen.Select(value => value.CommandId).Distinct().Count() != frozen.Length ||
            frozen.Where((value, index) => value.CommandId == Guid.Empty || value.Sequence != index ||
                !Enum.IsDefined(value.Engine) || !Enum.IsDefined(value.Kind) || !Enum.IsDefined(value.Observation) ||
                value.Engine == EngineOutcome.Running || !Reason(value.ReasonCode) ||
                value.Engine == EngineOutcome.NotStarted && value.Observation != TargetCommandObservation.Unverified ||
                value.Observation == TargetCommandObservation.OwnedHandoff && (value.Engine != EngineOutcome.Unknown || value.Kind != TargetCommandKind.Effect) ||
                value.Observation == TargetCommandObservation.ExpectedOffline &&
                    (value.Kind != TargetCommandKind.ReadOnlyProbe || value.Engine is not (EngineOutcome.Succeeded or EngineOutcome.Failed))).Any())
            throw Invalid();
        if (target.Outcome == TargetExecutionOutcome.ControlledReady && frozen.IsEmpty) throw Invalid();
        var functional = target.Outcome switch
        {
            TargetExecutionOutcome.OwnedReady or TargetExecutionOutcome.ControlledReady => EngineOutcome.Succeeded,
            TargetExecutionOutcome.LaunchFailed or TargetExecutionOutcome.ReadinessTimedOut => EngineOutcome.Failed,
            TargetExecutionOutcome.ReadinessUnknown or TargetExecutionOutcome.UnverifiedDispatch => EngineOutcome.Unknown,
            _ => EngineOutcome.NotStarted
        };
        var cleanupEngine = cleanup.Outcome switch
        {
            TargetCleanupOutcome.Completed => EngineOutcome.Succeeded,
            TargetCleanupOutcome.Failed => EngineOutcome.Failed,
            TargetCleanupOutcome.Unverified => EngineOutcome.Unknown,
            _ => EngineOutcome.NotStarted
        };
        RequireCleanup(cleanup, frozen);
        var commandOutcomes = frozen.Select(value => (Fact: value, Engine: Contribution(value))).ToArray();
        var engine = AttemptFactsPolicy.AggregateEngine(commandOutcomes.Select(value => value.Engine)
            .Prepend(cleanupEngine).Prepend(functional).Prepend(primary.Engine).ToArray());
        var reason = primary.Engine == engine ? primary.ReasonCode : functional == engine ? target.ReasonCode :
            cleanupEngine == engine ? cleanup.ReasonCode : commandOutcomes.First(value => value.Engine == engine).Fact.ReasonCode;
        return new(primary with { }, target with { }, primary with { Engine = engine, ReasonCode = reason })
        { Commands = frozen, Cleanup = cleanup with { ConfirmationCommands = cleanup.ConfirmationCommands.ToImmutableArray() } };
    }

    private static EngineOutcome Contribution(TargetCommandFact value) => value.Observation == TargetCommandObservation.OwnedHandoff ? EngineOutcome.Succeeded : value.Engine == EngineOutcome.NotStarted
        ? EngineOutcome.NotStarted : value.Engine == EngineOutcome.Unknown || value.Observation is
            TargetCommandObservation.Unverified or TargetCommandObservation.Incomplete or TargetCommandObservation.Contradictory
            ? EngineOutcome.Unknown : value.Observation == TargetCommandObservation.ExpectedOffline ? EngineOutcome.Succeeded : value.Engine;

    private static void RequireCleanup(TargetCleanupFact cleanup, ImmutableArray<TargetCommandFact> commands)
    {
        if (!Enum.IsDefined(cleanup.Verification) || cleanup.ConfirmationCommands.IsDefault ||
            cleanup.ConfirmationCommands.Distinct().Count() != cleanup.ConfirmationCommands.Length ||
            cleanup.ConfirmationCommands.Any(id => id == Guid.Empty || !commands.Any(command => command.CommandId == id)) ||
            cleanup.Verification != TargetCleanupVerification.DeviceOffline && !cleanup.ConfirmationCommands.IsEmpty ||
            cleanup.Outcome == TargetCleanupOutcome.NotRequired &&
                (cleanup.Verification != TargetCleanupVerification.None || !cleanup.ConfirmationCommands.IsEmpty)) throw Invalid();
        if (cleanup.Outcome != TargetCleanupOutcome.Completed || cleanup.Verification != TargetCleanupVerification.DeviceOffline) return;
        var last = commands.TakeLast(2).ToArray();
        if (last.Length != 2 || !cleanup.ConfirmationCommands.SequenceEqual(last.Select(command => command.CommandId)) ||
            last.Any(command => command.Kind != TargetCommandKind.ReadOnlyProbe ||
                command.Observation != TargetCommandObservation.ExpectedOffline)) throw Invalid();
    }

    private static bool Reason(string? value) => value is { Length: >= 1 and <= 80 } &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    private static ArgumentException Invalid() => new("execution_target_facts_invalid");
}
