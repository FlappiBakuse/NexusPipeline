using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusPipeline.Modules.Plugins.Contracts;

internal sealed record TaskDefinition
{
    public required string Id { get; init; }
    public required string SourceKey { get; init; }
    public required string Name { get; init; }
    public required string? ParentId { get; init; }
    public required string Role { get; init; }
    public required bool Enabled { get; init; }
    public required int Order { get; init; }
    public required bool CountsAsUnit { get; init; }
    public required bool RequiredForParent { get; init; }
    public required string RetryUnitId { get; init; }
    public required string RetryRisk { get; init; }
    public required string[] Dependencies { get; init; }
    public required string Detection { get; init; }
    public string? ConfigRef { get; init; }
}

internal sealed record TaskDiagnostic(string Code, string Message, string? TaskId = null);
internal sealed record TaskSelectionField(string ResourceId, System.Text.Json.Nodes.JsonArray Selector, string Purpose);
internal sealed record TaskBehaviorField(string ResourceId, System.Text.Json.Nodes.JsonArray Selector);
internal sealed record TaskEvidence(string SourceId, int Epoch, long Sequence, string RuleId);
internal sealed record TaskLogRecord(string SourceId, int Epoch, long Sequence, string Text);
internal sealed record TaskLogBatch(TaskLogRecord[] Records, bool HasGap);

internal sealed record TaskDiscovery
{
    public required string ProtocolVersion { get; init; }
    public required string Type { get; init; }
    public required string Coverage { get; init; }
    public required TaskDefinition[] Tasks { get; init; }
    public required TaskDiagnostic[] Diagnostics { get; init; }
    public TaskSelectionField[] SelectionFields { get; init; } = [];
    public TaskBehaviorField[] BehaviorFields { get; init; } = [];
}

internal sealed record TaskPlan(string ProtocolVersion, string PlanId, string Origin, string PluginId,
    string PluginVersion, DateTimeOffset GeneratedAt, string Signature, string Coverage,
    TaskDefinition[] Tasks, TaskDiagnostic[] Diagnostics)
{
    public string BehaviorSignature { get; init; } = "";
    public TaskSelectionField[] SelectionFields { get; init; } = [];
}

internal sealed record TaskObservation
{
    public required string Id { get; init; }
    public required string TaskId { get; init; }
    public required int ExecutionOrdinal { get; init; }
    public required string Status { get; init; }
    public required string ReasonCode { get; init; }
    public required TaskEvidence[] Evidence { get; init; }
    public string? SkipKind { get; init; }
}

internal sealed record TaskObservationBatch
{
    public required string ProtocolVersion { get; init; }
    public required string Type { get; init; }
    public required string RunId { get; init; }
    public required string AttemptId { get; init; }
    public required TaskObservation[] Observations { get; init; }
    public required string RunBoundary { get; init; }
    public required TaskEvidence[] BoundaryEvidence { get; init; }
    public required TaskDiagnostic[] Diagnostics { get; init; }
    public System.Text.Json.Nodes.JsonObject? CursorState { get; init; }
}

internal static class TaskProtocolJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    internal static T Read<T>(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 1024 * 1024)
            throw new InvalidDataException("resource_limit: protocol result exceeds 1 MiB");
        return JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("protocol_error: null output");
    }
    internal static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    internal static T Copy<T>(T value) => Read<T>(Write(value));
}
