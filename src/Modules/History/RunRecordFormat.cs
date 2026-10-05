using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Modules.History;

internal enum TaskReportSemantics { DailyFlow, ProviderExecution }

internal static class RunRecordFormat
{
    private static readonly JsonSerializerOptions Options = new(JsonOpts.Default)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static JsonDocument CurrentDocument(string json)
    {
        var document = JsonDocument.Parse(json);
        try
        {
            UniqueMembers(document.RootElement);
            var root = document.RootElement;
            Require(root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("SchemaVersion", out var version)
                && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int number) && number == 1);
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    private static void UniqueMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) UniqueMembers(child);
        if (element.ValueKind != JsonValueKind.Object) return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in element.EnumerateObject())
        {
            Require(names.Add(member.Name));
            UniqueMembers(member.Value);
        }
    }

    internal static RunRecord Read(string json)
    {
        using var document = CurrentDocument(json);
        foreach (string name in new[] { "Id", "StartTime", "Status", "ScriptInstanceId", "UserId" })
            Require(document.RootElement.TryGetProperty(name, out _));
        var record = JsonSerializer.Deserialize<RunRecord>(document.RootElement, Options);
        Require(record is not null);
        Validate(record!);
        return record!;
    }

    internal static void Validate(RunRecord record)
    {
        Require(record.SchemaVersion == 1 && Guid.TryParseExact(record.Id, "N", out _)
            && record.StartTime != DateTime.MinValue && record.ScriptInstanceId is not null && record.UserId is not null
            && record.Status is "running" or "success" or "failed" or "partial" or "cancelled" or "skipped" or "blocked" or "unverified"
            && record.AttemptDetails is not null && record.PluginHistory is not null);
        if (record.TaskReport is { } report)
        {
            var mode = ReportSemantics(report);
            Require(report["runId"]?.GetValue<string>() == record.Id
                && report["userId"]?.GetValue<string>() == record.UserId
                && report["scriptInstanceId"]?.GetValue<string>() == record.ScriptInstanceId
                && report["originalPlan"] is JsonObject plan
                && plan["protocolVersion"]?.GetValue<string>() == (mode == TaskReportSemantics.DailyFlow ? "0.2.0" : "provider-execution-v1")
                && plan["semanticsVersion"]?.GetValue<string>() == report["semanticsVersion"]?.GetValue<string>());
        }
        Require(record.HistoryDirectory is not null && record.LogFile is not null);
        if (record.HistoryDirectory.Length > 0)
        {
            var segments = record.HistoryDirectory.Split(['/', '\\']);
            Require(segments.Length == 2 && segments.All(FileName));
            Require(FileName(record.LogFile) && record.LogFile.EndsWith(".json", StringComparison.Ordinal));
        }
        foreach (var attempt in record.AttemptDetails!)
        {
            Require(attempt is not null && attempt.Number > 0 && attempt.Screenshots is not null);
            Require(string.IsNullOrEmpty(attempt.LogFile) || FileName(attempt.LogFile));
            foreach (var screenshot in attempt.Screenshots!) Require(screenshot is not null && FileName(screenshot.FileName));
        }
    }

    internal static TaskReportSemantics ReportSemantics(JsonObject report)
    {
        if (report["schemaVersion"] is JsonValue value && value.TryGetValue<int>(out int version)
            && report["semanticsVersion"] is JsonValue semantics && semantics.TryGetValue<string>(out string? name))
        {
            if (version == 2 && name == "daily-flow-v1") return TaskReportSemantics.DailyFlow;
            if (version == 1 && name == "provider-execution-v1") return TaskReportSemantics.ProviderExecution;
        }
        throw new InvalidDataException("unsupported_history_format: 请使用完整备份的旧版查看旧历史，并在新目录重新配置。");
    }

    internal static bool IsCurrentReport(JsonObject report)
    {
        try
        {
            var semantics = ReportSemantics(report);
            return report["originalPlan"] is JsonObject plan
                && plan["protocolVersion"]?.GetValue<string>() == (semantics == TaskReportSemantics.DailyFlow ? "0.2.0" : "provider-execution-v1")
                && plan["semanticsVersion"]?.GetValue<string>() == report["semanticsVersion"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FormatException) { return false; }
    }

    internal static bool FileName(string? name) => !string.IsNullOrWhiteSpace(name)
        && name is not ("." or "..") && name == Path.GetFileName(name)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    internal static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid)
    {
        if (!valid) throw new InvalidDataException("unsupported_history_format: 请使用完整备份的旧版查看旧历史，并在新目录重新配置。");
    }
}
