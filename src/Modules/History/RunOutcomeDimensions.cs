using System.Text.Json.Serialization;

namespace NexusPipeline.Modules.History;

/// <summary>独立的运行事实；旧历史没有此对象时不得从展示状态推断已核验业务成功。</summary>
public sealed record RunOutcomeDimensions(
    string EngineStatus,
    string BusinessVerification,
    string ExecutionOutcome,
    string RecoveryOutcome)
{
    private static readonly HashSet<string> EngineStatuses = new(StringComparer.Ordinal)
        { "not_started", "running", "succeeded", "failed", "cancelled", "unknown" };
    private static readonly HashSet<string> BusinessVerifications = new(StringComparer.Ordinal)
        { "verified_succeeded", "verified_failed", "satisfied", "inapplicable", "unverified" };
    private static readonly HashSet<string> ExecutionOutcomes = new(StringComparer.Ordinal)
        { "not_started", "running", "completed", "failed", "cancelled" };
    private static readonly HashSet<string> RecoveryOutcomes = new(StringComparer.Ordinal)
        { "not_required", "pending", "restored", "quarantined" };

    public bool IsValid => EngineStatuses.Contains(EngineStatus)
        && BusinessVerifications.Contains(BusinessVerification)
        && ExecutionOutcomes.Contains(ExecutionOutcome)
        && RecoveryOutcomes.Contains(RecoveryOutcome);
}
