using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Settings;

namespace NexusPipeline.Core.Execution;

public sealed record ExecutionRetrySurface(ProviderRetryDeclaration? Provider, int MaximumAttempts,
    bool HasHooks, bool HasTarget, bool HasScriptJudgement, bool HasManagedConfiguration, bool HasRetainedLauncher)
{
    public ImmutableArray<RetryHookDeclaration> Hooks { get; init; } = [];
}

public static class RetryAuthorityPolicy
{
    public static FrozenRetryPolicy Capture(Guid runId, IReadOnlyList<ExecutionRetrySurface> surfaces,
        AutomaticRetryPreferences approval, DateTimeOffset deadlineUtc)
    {
        ArgumentNullException.ThrowIfNull(surfaces); ArgumentNullException.ThrowIfNull(approval);
        var saved = surfaces.ToArray();
        if (runId == Guid.Empty || deadlineUtc == default || deadlineUtc.Offset != TimeSpan.Zero ||
            saved.Length is < 1 or > 4096 || saved.Any(item => item is null || item.MaximumAttempts is < 1 or > 99) ||
            approval.BackoffSeconds is < 0 or > 86400) throw new ArgumentException("execution_retry_surface_invalid");
        foreach (var item in saved) _ = ExecutionProviderPolicy.FreezeRetry(item.Provider);
        var safe = RetryHookPolicy.Describe(saved).Safety == RetrySafety.Safe;
        return new(safe ? RetrySafety.Safe : RetrySafety.Unknown, approval.Enabled,
            safe ? saved.Min(item => item.MaximumAttempts) : 1, deadlineUtc,
            TimeSpan.FromSeconds(safe ? approval.BackoffSeconds : 0), runId.ToString("D"));
    }

    public static string BindingKey(Guid runId, string stepId)
    {
        if (runId == Guid.Empty || string.IsNullOrWhiteSpace(stepId) || stepId.Length > 120 || stepId.Any(char.IsControl))
            throw new ArgumentException("execution_retry_business_identity_invalid");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "nexus.execution.business/1/" + runId.ToString("D") + "/" + stepId)));
    }
}
