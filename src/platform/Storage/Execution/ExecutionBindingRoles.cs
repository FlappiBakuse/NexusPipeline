using System.Collections.Immutable;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionBindingRoles
{
    internal static ImmutableArray<string> Required(ExecutionPlanStep step)
    {
        var roles = ImmutableArray.CreateBuilder<string>();
        if (step.Hooks?.Any(hook => hook.Role == "preHook") == true) roles.Add("preHook");
        roles.Add("process");
        if (step.JudgementWorker is not null) roles.Add("judgement");
        if (step.Hooks?.Any(hook => hook.Role == "postHook") == true) roles.Add("postHook");
        return roles.ToImmutable();
    }

    internal static bool IsHook(string role) => role is "preHook" or "postHook";
    internal static string[] DispatchedSteps(IReadOnlyList<StepWriterState> jobs) =>
        jobs.Select(job => job.StepId).Distinct(StringComparer.Ordinal).ToArray();

    internal static ExecutionPlanHook Hook(ExecutionPlanStep step, string role) =>
        step.Hooks?.SingleOrDefault(hook => hook.Role == role) ?? throw new IOException("execution_hook_declaration_unbound");

    internal static (string Path, string Sha256) Image(ExecutionPlanStep step, string role) => role switch
    {
        "process" => (step.ExecutablePath, step.ImageSha256),
        "judgement" when step.JudgementWorker is not null => (step.JudgementWorker.Broker.Path, step.JudgementWorker.Broker.Sha256),
        "preHook" or "postHook" => (Hook(step, role).Command.Path, Hook(step, role).Command.Sha256),
        _ => throw new IOException("execution_writer_role_unbound")
    };

    internal static bool IsComplete(System.Text.Json.JsonElement journal, ExecutionPlan total, string stepId,
        ImmutableArray<StepWriterState>? knownJobs = null, IReadOnlyDictionary<string, StepResultState>? knownResults = null)
    {
        var step = total.Steps.Single(step => step.StepId == stepId);
        if (BindingSkipCodec.Read(journal, total).Any(item => item.StepId == stepId)) return true;
        var jobs = (knownJobs ?? WriterIntentCodec.ReadJobs(journal, total)).Where(job => job.StepId == stepId).ToArray();
        var suppressed = BindingSuppressionCodec.Read(journal, total).SingleOrDefault(item => item.StepId == stepId);
        if (jobs.Any(job => job.ClosedElapsedMilliseconds is null)) return false;
        var represented = jobs.Select(job => job.Role).Concat(suppressed?.Roles ?? []).Concat(RetryHookCodec.ResolvedRoles(journal, total, stepId)).ToHashSet(StringComparer.Ordinal);
        if (!represented.SetEquals(Required(step))) return false;
        var main = jobs.SingleOrDefault(job => job.Role == "process");
        if (main is not null && !(knownResults is null
                ? StepResultCodec.Read(journal, total).Any(result => result.Binding.StepId == stepId && result.IsCollected)
                : knownResults.TryGetValue(stepId, out var result) && result.IsCollected)) return false;
        var hooks = HookResultCodec.Read(journal, total);
        return jobs.Where(job => IsHook(job.Role)).All(job => hooks.Any(result => result.StepId == stepId && result.Role == job.Role));
    }

    internal static ExecutionStepFact Combine(System.Text.Json.JsonElement journal, ExecutionPlan total, ExecutionStepCompletion item, string? totalPlanSha256 = null)
    {
        var skipped = BindingSkipCodec.Read(journal, total).SingleOrDefault(value => value.StepId == item.StepId);
        if (skipped is not null) return BindingSkipCodec.Fact(skipped);
        var main = item.Result is { } result
            ? new ExecutionStepFact(item.StepId, result.Latest.Engine, result.Latest.Business, result.Latest.ReasonCode)
            : new(item.StepId, EngineOutcome.NotStarted, BusinessOutcome.Unverified, item.NotDispatchedReason ?? "execution_primary_not_dispatched");
        if (main.Engine == EngineOutcome.Failed && main.Business == BusinessOutcome.Succeeded)
            main = main with { ReasonCode = "execution_business_succeeded" };
        var providerStep = total.Steps.Single(step => step.StepId == item.StepId);
        if (providerStep.Provider is not null)
        {
            var terminal = ProviderTerminalCodec.Read(journal, total).SingleOrDefault(value => value.Binding.StepId == item.StepId);
            main = ProviderFactsPolicy.Compose(main, terminal?.Decision, item.Result?.Latest.Engine != EngineOutcome.NotStarted &&
                item.Result is not null);
        }
        if (total.Steps.Single(step => step.StepId == item.StepId).Target?.Kind == "pc")
        {
            var stages = PcStageCodec.Read(journal, total, totalPlanSha256 ?? throw new IOException("execution_target_total_reference_required"));
            var target = stages.SingleOrDefault(stage => stage.Binding.StepId == item.StepId);
            if (target is not null) main = TargetFactsPolicy.Compose(main, new(target.Outcome, target.ReasonCode)).Combined;
            else if (TargetJobCodec.Read(journal, total, totalPlanSha256!).Any(job => job.Binding.StepId == item.StepId))
                main = TargetFactsPolicy.Compose(main, new(TargetExecutionOutcome.UnverifiedDispatch,
                    "execution_target_dispatch_unverified")).Combined;
        }
        if (total.Steps.Single(step => step.StepId == item.StepId).Target?.Kind == "android")
        {
            var digest = totalPlanSha256 ?? throw new IOException("execution_target_total_reference_required");
            var startup = AndroidStartupCodec.Read(journal, total, digest).SingleOrDefault(stage => stage.Binding.StepId == item.StepId);
            var collected = AndroidCollectedCodec.Read(journal, total, digest).SingleOrDefault(stage => stage.Binding.StepId == item.StepId);
            if (startup is not null) main = TargetFactsPolicy.Compose(main, new(startup.Outcome, startup.ReasonCode),
                collected?.Commands ?? startup.Commands, collected?.Cleanup).Combined;
            else if (AndroidControlCodec.Read(journal, total, digest).Any(command => command.Binding.StepId == item.StepId))
                main = TargetFactsPolicy.Compose(main, new(TargetExecutionOutcome.UnverifiedDispatch,
                    "execution_target_dispatch_unverified")).Combined;
        }
        if (providerStep.Provider?.Launcher is not null)
        {
            var launcher = ProviderLauncherCodec.ReadAll(journal, total,
                totalPlanSha256 ?? throw new IOException("execution_provider_launcher_total_required")).LastOrDefault(value => value.Binding.StepId == item.StepId);
            if (launcher is not null)
            {
                var engine = launcher.Stage is "retained" or "recoveredRetained" ? EngineOutcome.Succeeded : launcher.Stage is "stopped" or "recoveredStopped"
                    ? launcher.LaunchEngine!.Value : EngineOutcome.Unknown;
                var reason = engine switch
                {
                    EngineOutcome.Succeeded => "execution_provider_launcher_started",
                    EngineOutcome.Failed => "execution_provider_launcher_start_failed",
                    EngineOutcome.NotStarted => "execution_provider_launcher_not_started",
                    _ => "execution_provider_launcher_dispatch_unverified"
                };
                main = ProviderFactsPolicy.ComposeLauncher(main, engine, reason);
            }
        }
        var hooks = HookResultCodec.Read(journal, total).Where(hook => hook.StepId == item.StepId).Select(hook =>
            new HookEngineFact(hook.Role == "preHook" ? ExecutionHookRole.Pre : ExecutionHookRole.Post, hook.Engine, hook.ReasonCode)).ToImmutableArray();
        hooks = hooks.AddRange(RetryHookCodec.Read(journal, total).Where(hook => hook.StepId == item.StepId && hook.Role == "preHook")
            .Select(_ => new HookEngineFact(ExecutionHookRole.Pre, EngineOutcome.Succeeded, "execution_pre_hook_inherited")));
        hooks = hooks.OrderBy(hook => hook.Role).ToImmutableArray();
        var combined = hooks.IsEmpty ? main : HookFactsPolicy.Compose(main, hooks).Combined;
        return BindingTimeoutCodec.Read(journal, total).Any(timeout => timeout.StepId == item.StepId)
            ? combined with { Engine = EngineOutcome.Failed, ReasonCode = BindingTimeoutCodec.Reason } : combined;
    }
}
