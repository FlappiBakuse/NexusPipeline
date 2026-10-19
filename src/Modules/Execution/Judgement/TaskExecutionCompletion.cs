using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Modules.Execution.Judgement;

internal sealed record TaskExecutionCompletion(string Status, string Basis, int? ExitCode)
{
    internal bool ObservedWorkersExitedNormally { get; init; }

    internal TaskExecutionCompletion ConfirmBoundary(bool fullyVerified, bool stoppedAtBoundary)
        => Status == "unconfirmed" && fullyVerified && (ObservedWorkersExitedNormally || stoppedAtBoundary)
            ? new("normal", "verified_task_boundary", null) : this;

    internal static TaskExecutionCompletion Unconfirmed { get; } = new("unconfirmed", "unconfirmed_worker", null);

    internal static TaskExecutionCompletion FromOwnedProcesses(OwnedProcessCompletion? completion,
        ProcessIdentity? root, ProcessRole role, string? game, IReadOnlySet<string>? workerImages = null)
    {
        if (completion is null || root is null) return Unconfirmed;
        var main = completion.Processes.SingleOrDefault(item => root.Value.Matches(item.Identity));
        if (main is null) return Unconfirmed;
        if (main.ExitCode is not null and not 0) return new("failed", "owned_process_nonzero", main.ExitCode);
        var workers = completion.Processes.Where(item => (role != ProcessRole.GameLauncher && root.Value.Matches(item.Identity)
            || !root.Value.Matches(item.Identity) && !ProcessRoleClassifier.IsConsoleSidecar(item.Identity)
            && (game is null || !ProcessTree.IsSameProcessName(item.Identity.ImageName, game)))).ToArray();
        var automation = workers.Where(item => root.Value.Matches(item.Identity)
            || workerImages?.Contains(item.Identity.ImageName) == true
            || role == ProcessRole.AutomationWorker && string.Equals(item.Identity.ImageName, root.Value.ImageName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (automation.Any(item => item.ExitCode is not null and not 0))
            return new("failed", "owned_process_nonzero", automation.First(item => item.ExitCode is not null and not 0).ExitCode);
        if (role != ProcessRole.GameLauncher && main.ExitCode is null) return Unconfirmed;
        bool workerConfirmed = role == ProcessRole.AutomationWorker || role == ProcessRole.GameLauncher
            && workerImages is { Count: > 0 } && workers.Any(item => workerImages.Contains(item.Identity.ImageName));
        if (!workerConfirmed || workers.Length == 0 || workers.Any(item => item.ExitCode is null)) return Unconfirmed;
        // A child can exit between Job samples. Complete task evidence may supplement
        // missing historical handles, but never a live or failed identified worker.
        if (!completion.Complete) return Unconfirmed with { ObservedWorkersExitedNormally = true };
        return new("normal", "owned_worker_exit", 0);
    }
}
