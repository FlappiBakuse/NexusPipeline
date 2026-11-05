using System.Collections.Immutable;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Settings;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionRetryAuthority
{
    internal static ExecutionPlanRetryPolicy Capture(ExecutionPlan plan, IReadOnlyDictionary<string, int> maximumAttempts,
        AutomaticRetryPreferences approval)
    {
        var initialize = ProjectInitializationPlan.IsInitialization(plan);
        if (initialize) approval = new(false, 0);
        var policy = RetryAuthorityPolicy.Capture(Guid.Parse(plan.RunId), plan.Steps.Select(step => Surface(step, maximumAttempts[step.ScriptId])).ToArray(), approval,
            plan.Budget.TotalTimeoutSeconds == 0 ? DateTimeOffset.MaxValue :
                DateTimeOffset.Parse(plan.CreatedAt, System.Globalization.CultureInfo.InvariantCulture).AddSeconds(plan.Budget.TotalTimeoutSeconds));
        return new()
        {
            Safety = policy.Safety == RetrySafety.Safe ? "safe" : "unknown", HostApproved = policy.HostApproved,
            MaximumAttempts = policy.MaximumAttempts, DeadlineUtc = policy.DeadlineUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"),
            BackoffSeconds = (long)policy.Backoff.TotalSeconds, IdempotencyKey = policy.IdempotencyKey
        };
    }

    internal static void RequireFrozenSettings(Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction, ExecutionPlan plan)
    {
        var settings = HostSettingsRows.Read(connection, transaction, plan.SettingsRevision).Values.AutomaticRetry;
        if (plan.RetryPolicy.HostApproved != (!ProjectInitializationPlan.IsInitialization(plan) && settings.Enabled) ||
            plan.RetryPolicy.BackoffSeconds != (plan.RetryPolicy.Safety == "safe" ? settings.BackoffSeconds : 0))
            throw new IOException("execution_retry_settings_unbound");
    }

    internal static void Validate(ExecutionPlan plan, AutomationBundle bundle)
    {
        var maximum = bundle.Scripts.ToDictionary(script => script.Id, script => checked((int)script.Limits.MaximumAttempts), StringComparer.Ordinal);
        var expected = Capture(plan, maximum, new(plan.RetryPolicy.HostApproved, checked((int)plan.RetryPolicy.BackoffSeconds)));
        if (!System.Text.Json.JsonElement.DeepEquals(System.Text.Json.JsonSerializer.SerializeToElement(expected),
            System.Text.Json.JsonSerializer.SerializeToElement(plan.RetryPolicy))) throw new IOException("execution_retry_authority_unbound");
    }

    internal static RetryAuthorityDescription Describe(ExecutionPlan plan) => RetryHookPolicy.Describe(
        plan.Steps.Select(step => Surface(step, checked((int)plan.RetryPolicy.MaximumAttempts))).ToArray());

    private static ExecutionRetrySurface Surface(ExecutionPlanStep step, int maximumAttempts) =>
        new(step.Purpose == "initialization" ? null : step.Provider?.Retry is { } retry ? Read(retry) : null,
            step.Purpose == "initialization" ? 1 : maximumAttempts, step.Hooks is { Count: > 0 },
            step.Target is not null, step.JudgementWorker is not null, step.ConfigurationTargets.Count != 0,
            step.Provider?.Launcher is not null) { Hooks = Hooks(step) };

    internal static ImmutableArray<RetryHookDeclaration> Hooks(ExecutionPlanStep step) =>
        (step.Hooks ?? []).Select(hook => new RetryHookDeclaration(hook.Role == "preHook" ? ExecutionHookRole.Pre : ExecutionHookRole.Post,
            hook.OnceAfterSuccess, hook.FinalOnly)).ToImmutableArray();

    private static ProviderRetryDeclaration Read(ExecutionProviderRetry value) =>
        ExecutionProviderPolicy.FreezeRetry(new(value.Safety == "safe" ? RetrySafety.Safe : RetrySafety.Unknown,
            value.Scope, value.IdempotencyProtocol, value.Outcomes.Select(outcome =>
                Enum.Parse<AttemptOutcome>(outcome, true)).ToImmutableArray())) ?? throw new IOException("execution_retry_authority_unbound");
}
