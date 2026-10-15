using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Execution.Judgement;

internal sealed record TaskRunVerification(string Mode, int KnownTaskCount, int OpaqueTaskCount,
    int OpaqueScopeCount, string[] UnverifiedTaskIds, string[] OpaqueScopeIds, string[] ReasonCodes,
    TaskExecutionCompletion Completion, bool UnknownRetainedOnRetry)
{
    internal static TaskRunVerification Create(TaskPlan plan, TaskExecutionCompletion completion, bool unknownRetainedOnRetry)
    {
        var tasks = plan.Tasks.Where(task => task.Enabled).ToArray();
        var unknown = tasks.Where(task => task.Recognition == "opaque").Select(task => task.Id).ToArray();
        var scopes = plan.Verification!.OpaqueScopes.Where(scope => scope.Enabled).ToArray();
        int known = tasks.Count(task => task.Recognition == "known");
        string mode = unknown.Length + scopes.Length == 0 ? "complete" : known == 0 ? "opaque" : "partial";
        return new(mode, known, unknown.Length, scopes.Length, unknown, scopes.Select(scope => scope.Id).ToArray(),
            scopes.Select(scope => scope.ReasonCode).Concat(unknown.Length > 0 ? new[] { "tasks.not_individually_verified" } : [])
                .Distinct(StringComparer.Ordinal).ToArray(), completion, unknownRetainedOnRetry);
    }

    internal RunAttemptResult Resolve(TaskSummary summary)
    {
        if (Completion.Status != "normal")
            return RunAttemptResult.Failed("execution." + Completion.Basis, "execution." + Completion.Basis);
        if (summary.Tone == "bad") return RunAttemptResult.Failed(summary.Outcome, "tasks." + summary.Outcome);
        if (summary.Tone == "warn") return RunAttemptResult.Partial(summary.Outcome, "tasks.partial_failure");
        if (summary.Tone != "ok" && summary.Outcome != "no_tasks")
            return RunAttemptResult.Failed("tasks.incomplete", "tasks.incomplete");
        if (OpaqueTaskCount + OpaqueScopeCount > 0)
        {
            string reason = KnownTaskCount == 0 ? "execution.success_process_exit_only" : "execution.success_with_partial_verification";
            return RunAttemptResult.Success(reason, reason);
        }
        return summary.Counts["succeeded"] > 0 ? RunAttemptResult.Success("tasks.all_satisfied", "tasks.all_satisfied")
            : new RunAttemptResult { Status = "skipped", Reason = "tasks.no_execution_required", ReasonCode = "tasks.no_execution_required" };
    }
}
