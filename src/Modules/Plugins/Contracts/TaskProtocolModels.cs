using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusPipeline.Modules.Plugins.Contracts;

internal sealed record TaskDefinition
{
    public required string Id { get; init; }
    public required string SourceKey { get; init; }
    public required string Name { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Text.Json.Nodes.JsonObject? NameText { get; init; }
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

internal sealed record TaskDiagnostic(string Code, string Message, string? TaskId = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Text.Json.Nodes.JsonObject? ReasonText { get; init; }
}
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskConfigAssessment? ConfigAssessment { get; init; }
}

internal sealed record TaskPlan(string ProtocolVersion, string PlanId, string Origin, string PluginId,
    string PluginVersion, DateTimeOffset GeneratedAt, string Signature, string Coverage,
    TaskDefinition[] Tasks, TaskDiagnostic[] Diagnostics)
{
    public string BehaviorSignature { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskDisplaySnapshot? DisplaySnapshot { get; init; }
    public TaskSelectionField[] SelectionFields { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskConfigAssessment? ConfigAssessment { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskReadiness? CurrentReadiness { get; init; }
}

internal sealed record TaskObservation
{
    public required string Id { get; init; }
    public required string TaskId { get; init; }
    public required int ExecutionOrdinal { get; init; }
    public required string Status { get; init; }
    public required string ReasonCode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Text.Json.Nodes.JsonObject? ReasonText { get; init; }
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskIncident[]? Incidents { get; init; }
}

internal sealed record TaskIncident
{
    public required string Id { get; init; }
    public required string? TaskId { get; init; }
    public required string ScopeId { get; init; }
    public required int ExecutionOrdinal { get; init; }
    public required string Kind { get; init; }
    public required string Resolution { get; init; }
    public required string ReasonCode { get; init; }
    public required TaskEvidence[] Evidence { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Text.Json.Nodes.JsonObject? ReasonText { get; init; }
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
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        Check(document.RootElement, null);
        return JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("protocol_error: null output");
    }
    private static void Check(JsonElement element, string? protocolVersion)
    {
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) Check(child, protocolVersion);
        if (element.ValueKind != JsonValueKind.Object) return;
        if (element.TryGetProperty("protocolVersion", out var version) && version.ValueKind == JsonValueKind.String
            && (element.TryGetProperty("type", out _) || element.TryGetProperty("planId", out _))) protocolVersion = version.GetString();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new InvalidDataException("protocol_error: duplicate member");
            if (protocolVersion == "1.0" && property.Name is "nameText" or "reasonText" or "incidents")
                throw new InvalidDataException("protocol_error: text references require 1.1");
            if (protocolVersion is "1.0" or "1.1" && property.Name is "configAssessment" or "currentReadiness")
                throw new InvalidDataException("protocol_error: configuration diagnostics require 1.2");
            if (property.Name is "__proto__" or "prototype" or "constructor")
                throw new InvalidDataException("protocol_error: reserved member");
            Check(property.Value, protocolVersion);
        }
    }
    internal static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    internal static T Copy<T>(T value) => Read<T>(Write(value));
}
