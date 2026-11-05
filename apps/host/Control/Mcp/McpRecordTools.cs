using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Primitives;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Records;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Host.Control.Records;
using NexusPipeline.Host.Control.Runtime;
using NexusPipeline.Host.Control.Notifications;

namespace NexusPipeline.Host.Control.Mcp;

[SupportedOSPlatform("windows")]
internal sealed class McpRecordTools(RecordUseCases records, RuntimeUseCases runtime)
{
    internal static readonly string[] Names =
        ["status", "doctor", "records_list", "record_attempts", "record_logs", "record_log_tail", "record_images"];
    internal IReadOnlyList<McpToolDescription> List(AuthenticatedControlRequest request)
    {
        var visible = request.Visible().Select(value => value.Id).ToHashSet();
        return Array.AsReadOnly(Names.Where(name => visible.Contains(Capability(name)))
            .Select(name => new McpToolDescription(name, name.Replace('_', ' '),
                name == "record_log_tail" ? "Read at most 65536 original log bytes." :
                    "Read bounded saved metadata from the original owner.", Schema(name), new(true, false, true, false))).ToArray());
    }

    internal object Call(AuthenticatedControlRequest request, JsonElement parameters)
    {
        if (!parameters.TryGetProperty("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String ||
            !parameters.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
            throw new McpProtocolException(-32602, "tool_arguments_invalid");
        var name = nameValue.GetString()!;
        request.Require(Capability(name));
        var query = Query(arguments, Fields(name));
        object result = name switch
        {
            "status" => runtime.Status(request),
            "doctor" => runtime.Doctor(request),
            "records_list" => ReadRecords(request, query),
            "record_attempts" => records.Attempts(request, new(Id(query, "runId"),
                query.Integer("afterOrdinal", 0), query.Integer("limit", 50))),
            "record_logs" => records.Logs(request, new(Id(query, "runId"), Id(query, "attemptId"),
                query.Integer("afterPosition", -1), query.Integer("limit", 50))),
            "record_log_tail" => records.Tail(request, new(Id(query, "runId"), Id(query, "attemptId"),
                query.Text("stepId") ?? throw Invalid(), ControlQueryValues.Role(query.Text("role") ?? throw Invalid())),
                query.Integer("maximumBytes", 65536)),
            "record_images" => records.Images(request, new(Id(query, "runId"), Id(query, "attemptId"),
                query.Integer("afterPosition", -1), query.Integer("limit", 50))),
            _ => throw new McpProtocolException(-32602, "tool_unknown")
        };
        return new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result,
            NotificationUseCases.Json) } }, structuredContent = result, isError = false };
    }

    private object ReadRecords(AuthenticatedControlRequest request, ControlQueryValues query)
    {
        var after = query.Utc("afterEndedAt"); var run = query.OptionalId("afterRunId");
        if ((after is null) != (run is null)) throw Invalid();
        return records.Read(request, new(query.Integer("limit", 50), query.OptionalId("automationId"), query.OptionalId("userId"),
            query.Business("business"), after is null ? null : new(after.Value, run!.Value),
            query.Utc("endedFromInclusive"), query.Utc("endedUntilExclusive")));
    }
    private static ControlCapability Capability(string name) => name switch
    {
        "status" => ControlCapability.StatusRead, "doctor" => ControlCapability.DoctorRead,
        "records_list" or "record_attempts" => ControlCapability.RecordsRead,
        "record_logs" or "record_log_tail" => ControlCapability.LogsRead, "record_images" => ControlCapability.ImagesRead,
        _ => throw new McpProtocolException(-32602, "tool_unknown")
    };
    private static string[] Fields(string name) => name switch
    {
        "status" or "doctor" => [],
        "records_list" => ["limit", "automationId", "userId", "business", "afterEndedAt", "afterRunId", "endedFromInclusive", "endedUntilExclusive"],
        "record_attempts" => ["runId", "afterOrdinal", "limit"],
        "record_logs" or "record_images" => ["runId", "attemptId", "afterPosition", "limit"],
        "record_log_tail" => ["runId", "attemptId", "stepId", "role", "maximumBytes"],
        _ => throw new McpProtocolException(-32602, "tool_unknown")
    };
    private static Guid Id(ControlQueryValues query, string name) => query.OptionalId(name) ?? throw Invalid();
    private static ControlQueryValues Query(JsonElement arguments, string[] fields)
    {
        ControlRequestBody.RequireMembers(arguments, fields);
        var values = new Dictionary<string, StringValues>(StringComparer.Ordinal);
        foreach (var value in arguments.EnumerateObject())
            values.Add(value.Name, value.Value.ValueKind switch
            {
                JsonValueKind.String => value.Value.GetString(),
                JsonValueKind.Number => value.Value.GetRawText(),
                _ => throw Invalid()
            });
        var query = new ControlQueryValues(new QueryCollection(values), fields); query.Validate(); return query;
    }
    private static JsonElement Schema(string name)
    {
        var fields = Fields(name);
        var required = fields.Where(field => field is "runId" or "attemptId" or "stepId" or "role").ToArray();
        var properties = fields.ToDictionary(field => field, field => Property(name, field), StringComparer.Ordinal);
        return JsonSerializer.SerializeToElement(new { type = "object", required, properties, additionalProperties = false });
    }
    private static object Property(string name, string field) => field switch
    {
        "limit" => new { type = "integer", minimum = 1, maximum = name == "records_list" ? 200 : 100 },
        "afterPosition" => new { type = "integer", minimum = -1, maximum = 4095 },
        "afterOrdinal" => new { type = "integer", minimum = 0, maximum = 99 },
        "maximumBytes" => new { type = "integer", minimum = 1, maximum = 65536 },
        "business" => new { type = "string", @enum = new[] { "unverified", "succeeded", "partial", "failed", "notApplicable", "cancelled" } },
        "role" => new { type = "string", @enum = new[] { "main", "preHook", "postHook" } },
        _ => new { type = "string", minLength = 1, maxLength = 256 }
    };
    private static ControlRequestException Invalid() => new(400, "query_invalid");
}
