using System.Collections.Immutable;

namespace NexusPipeline.Core.Execution;

public sealed record RetryHookDeclaration(ExecutionHookRole Role, bool OnceAfterSuccess, bool FinalOnly);
public enum RetryHookDisposition { Collected, InheritedSuccess, Deferred, NotDispatched }
public sealed record RetryHookFact(ExecutionHookRole Role, RetryHookDisposition Disposition, EngineOutcome Engine);
public sealed record RetryAuthorityDescription(RetrySafety Safety, string Scope, string ReasonCode);

public static class RetryHookPolicy
{
    public static RetryAuthorityDescription Describe(IReadOnlyList<ExecutionRetrySurface> surfaces)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        if (surfaces.Count == 0) throw new ArgumentException("execution_retry_surface_invalid");
        foreach (var surface in surfaces)
        {
            ArgumentNullException.ThrowIfNull(surface);
            if (surface.Hooks.IsDefault || surface.Hooks.Length > 2 || surface.Hooks.Any(hook => hook is null || !Enum.IsDefined(hook.Role)) ||
                surface.Hooks.Select(hook => hook.Role).Distinct().Count() != surface.Hooks.Length ||
                !surface.Hooks.Select(hook => hook.Role).SequenceEqual(surface.Hooks.Select(hook => hook.Role).Order()) ||
                surface.Hooks.Any(hook => hook.Role == ExecutionHookRole.Pre ? hook.FinalOnly : hook.OnceAfterSuccess))
                throw new ArgumentException("execution_retry_hook_declaration_invalid");
        }
        if (surfaces.Any(surface => surface.Provider is null || surface.HasTarget || surface.HasScriptJudgement || surface.HasRetainedLauncher))
            return new(RetrySafety.Unknown, "none", "retry_effects_unverified");
        if (surfaces.Any(surface => surface.HasHooks != !surface.Hooks.IsEmpty ||
            surface.Hooks.Any(hook => hook.Role == ExecutionHookRole.Pre ? !hook.OnceAfterSuccess : !hook.FinalOnly)))
            return new(RetrySafety.Unknown, "none", "retry_hook_effects_unverified");
        if (surfaces.Count != 1 && surfaces.Any(surface => surface.Hooks.Any(hook => hook.Role == ExecutionHookRole.Post)))
            return new(RetrySafety.Unknown, "none", "retry_hook_finality_unverified");
        return new(RetrySafety.Safe, surfaces.Any(surface => surface.HasHooks) ? "once-pre-final-post" : "whole-worker",
            "retry_authority_verified");
    }

    public static bool CanRetry(IReadOnlyList<RetryHookDeclaration> declarations, IReadOnlyList<RetryHookFact> facts)
    {
        ArgumentNullException.ThrowIfNull(declarations); ArgumentNullException.ThrowIfNull(facts);
        if (facts.Any(fact => fact is null || !Enum.IsDefined(fact.Role) || !Enum.IsDefined(fact.Disposition) ||
                !Enum.IsDefined(fact.Engine) || fact.Engine == EngineOutcome.Running) ||
            facts.Select(fact => fact.Role).Distinct().Count() != facts.Count ||
            facts.Any(fact => !declarations.Any(declaration => declaration.Role == fact.Role)))
            throw new ArgumentException("execution_retry_hook_facts_invalid");
        return declarations.All(declaration =>
        {
            var fact = facts.SingleOrDefault(item => item.Role == declaration.Role);
            if (declaration.Role == ExecutionHookRole.Pre)
                return declaration.OnceAfterSuccess && (fact is { Engine: EngineOutcome.Succeeded,
                    Disposition: RetryHookDisposition.Collected or RetryHookDisposition.InheritedSuccess } ||
                    fact is { Engine: EngineOutcome.NotStarted,
                    Disposition: RetryHookDisposition.Collected or RetryHookDisposition.NotDispatched });
            return declaration.FinalOnly && (fact is null || fact is { Engine: EngineOutcome.NotStarted,
                Disposition: RetryHookDisposition.Deferred or RetryHookDisposition.NotDispatched or RetryHookDisposition.Collected });
        });
    }
}
