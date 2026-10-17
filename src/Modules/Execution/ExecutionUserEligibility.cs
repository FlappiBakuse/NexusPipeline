using NexusPipeline.Modules.Users.Contracts;

namespace NexusPipeline.Modules.Execution;

/// <summary>执行用户的现行限制规则。解释计划与真实 runner 共用这条规则，避免 dry-run 与启动分歧。</summary>
internal static class ExecutionUserEligibility
{
    internal static bool HasNoEnabledBindings(IReadOnlyList<PlannedQueueTask> tasks) =>
        tasks.Count > 0 && tasks.All(task => task.Script is not null && task.ResolvedUsers?.Count == 0);

    public static bool HasReachedDailySuccessCap(ResolvedScriptUser user, int successfulRunsToday)
    {
        int maximum = user.Binding.MaxSuccessfulRunsPerDay;
        return maximum > 0 && successfulRunsToday >= maximum;
    }
}
