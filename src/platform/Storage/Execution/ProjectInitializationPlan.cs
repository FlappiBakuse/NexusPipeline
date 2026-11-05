using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ProjectInitializationPlan
{
    internal static bool IsInitialization(ExecutionPlan plan)
    {
        if (!plan.Steps.Any(step => step.Purpose is not null)) return false;
        if (plan.Steps.Count != 1 || plan.Selection.Count != 1 || plan.QueueId is not null ||
            plan.CompletionAction != "none" || plan.JudgementNetwork is not null || plan.CaptureRuntime is not null)
            throw Invalid();
        var step = plan.Steps[0];
        if (step.Purpose != "initialization" || step.EngineKind != "extensionWorker" || step.Provider is null ||
            step.Provider.Launcher is not null || step.Adapter?.Compiled is null || step.BindingId is null ||
            step.Target is not null || step.JudgementWorker is not null || step.Hooks is { Count: > 0 } ||
            step.LogTargets.Count != 0 || step.UpdateConfigurationAtFinish || step.DependsOn.Count != 0 ||
            step.MaximumAttempts != 1 || step.MaximumSuccessfulRunsPerDay != -1 || step.SelectedTaskIds.Count == 0 ||
            step.Tasks.Any(task => task.WorkflowRole != "technical" || !task.IsEnabled || !task.IsSelected ||
                task.Dependencies.Count != 0 || task.ExecutionTaskId is not null))
            throw Invalid();
        return true;
    }

    internal static void RequireRequest(ExecutionPlan plan, ExecutionSelectionRequest request)
    {
        var initialize = IsInitialization(plan);
        if (initialize != (request.Target is ProjectInitializationTarget) || initialize &&
            plan.Steps[0].BindingId != ((ProjectInitializationTarget)request.Target).BindingId.ToString("D"))
            throw Invalid();
    }

    private static IOException Invalid() => new("execution_initialization_unbound");
}
