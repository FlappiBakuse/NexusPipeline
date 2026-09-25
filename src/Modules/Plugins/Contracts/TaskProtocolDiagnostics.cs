using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NexusPipeline.Modules.Plugins.Contracts;

/// <summary>Manifest-owned configuration rule declaration for taskProtocol 0.1.0.</summary>
internal sealed record TaskConfigRuleDescriptor(string RuleId, bool Required, string Criticality);

/// <summary>Manifest-owned, non-arbitrary environment target declaration.</summary>
internal sealed record TaskEnvironmentCheckDescriptor(
    string Id,
    string SourceKind,
    string? ResourceId,
    JsonArray? Selector,
    string? HostField,
    string ExpectedKind,
    string RelativeBase,
    bool NetworkAccess,
    bool FollowReparsePoints,
    string Comparison = "exact",
    JsonArray? SecondarySelector = null,
    string? DefaultValue = null,
    string? SecondaryDefaultValue = null);

internal sealed record TaskExecutionContext(
    string UserId,
    string ScriptInstanceId,
    string BindingKey,
    string Trigger,
    string Mode,
    string LaunchOwner,
    TaskGameTarget GameTarget,
    TaskQueueContext Queue,
    TaskCleanupContext Cleanup,
    TaskEffectiveLaunch EffectiveLaunch,
    TaskLogSourceContext LogSource)
{
    internal static TaskExecutionContext Unknown(string userId, string scriptInstanceId, string trigger) => new(
        userId,
        scriptInstanceId,
        scriptInstanceId,
        trigger,
        "unknown",
        "unknown",
        new("none", null, null),
        new("unknown", "unknown"),
        new(false, false, "none"),
        new("unknown", null, null, false, null),
        new("unknown", false));
}

internal sealed record TaskGameTarget(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InspectionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Ready = null);

internal sealed record TaskQueueContext(string Kind, string HasFollowingWork);

internal sealed record TaskCleanupContext(bool HostWillTerminateScript, bool HostWillCloseGame, string HostManagedSystemAction);

internal sealed record TaskEffectiveLaunch(
    string EntryId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Autostart,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TaskIndex,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ExitRequested,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SelectedProfile);

/// <summary>
/// Host-owned, read-only fact about the log channel available to the task
/// protocol. Launch ownership is not evidence that a usable log source exists.
/// </summary>
internal sealed record TaskLogSourceContext(string Kind, bool Available);

/// <summary>Safe result returned by nexus.inspectDeclaredTarget(id). It never contains file contents.</summary>
internal sealed record TaskEnvironmentInspection(
    string InspectionId,
    string Status,
    string ExpectedKind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActualKind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? MatchesContext,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason);

internal sealed record TaskConfigAssessment(string SchemaVersion, TaskConfigCheck[] Checks);

internal sealed record TaskConfigCheck(
    string RuleId,
    string Evaluation,
    string Severity,
    string ExecutionEffect,
    JsonObject Scope,
    JsonArray Locations,
    JsonArray Actions)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? ReasonText { get; init; }
}

internal sealed record TaskReadiness(
    string State,
    bool Stale,
    DateTimeOffset CheckedAt,
    string AssessmentId,
    string ConfigRevision,
    string ContextFingerprint);

internal sealed class TaskAdmissionBlockedException : Exception
{
    internal TaskAdmissionBlockedException(TaskReadiness readiness, TaskConfigAssessment assessment)
        : base("task admission blocked by configuration assessment")
    {
        Readiness = readiness;
        Assessment = assessment;
    }

    internal TaskReadiness Readiness { get; }
    internal TaskConfigAssessment Assessment { get; }
}
