using System.Collections.Immutable;

namespace NexusPipeline.Core.Execution;

public enum ExecutionHookRole { Pre, Post }
public sealed record HookEngineFact(ExecutionHookRole Role, EngineOutcome Engine, string ReasonCode);
public sealed record ExecutionHookComposition(ExecutionStepFact Primary, ImmutableArray<HookEngineFact> Hooks,
    ExecutionStepFact Combined);

public static class HookFactsPolicy
{
    public static ExecutionHookComposition Compose(ExecutionStepFact primary, IReadOnlyList<HookEngineFact> hooks)
    {
        ArgumentNullException.ThrowIfNull(primary); ArgumentNullException.ThrowIfNull(hooks);
        var frozen = hooks.ToImmutableArray();
        if (string.IsNullOrWhiteSpace(primary.StepId) || primary.StepId.Length > 120 || primary.StepId.Any(char.IsControl) ||
            !Enum.IsDefined(primary.Business) || !Reason(primary.ReasonCode) || frozen.Length > 2 ||
            frozen.Any(hook => hook is null || !Enum.IsDefined(hook.Role) || !Reason(hook.ReasonCode)) ||
            frozen.Select(hook => hook.Role).Distinct().Count() != frozen.Length ||
            !frozen.Select(hook => hook.Role).SequenceEqual(frozen.Select(hook => hook.Role).Order()) ||
            primary.Engine == EngineOutcome.NotStarted && primary.Business is BusinessOutcome.Succeeded or BusinessOutcome.Partial or BusinessOutcome.Failed)
            throw new ArgumentException("execution_hook_facts_invalid");
        var engine = AttemptFactsPolicy.AggregateEngine(new[] { primary.Engine }.Concat(frozen.Select(hook => hook.Engine)).ToArray());
        var decisive = frozen.FirstOrDefault(hook => hook.Engine == engine && engine is EngineOutcome.Unknown or EngineOutcome.Failed or EngineOutcome.Cancelled);
        var combined = primary with { Engine = engine, ReasonCode = decisive?.ReasonCode ?? primary.ReasonCode };
        return new(primary with { }, frozen, combined);
    }

    private static bool Reason(string? value) => value is { Length: >= 1 and <= 80 } &&
        value[0] is >= 'a' and <= 'z' && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}
