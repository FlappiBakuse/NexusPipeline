using System.Text.Json.Nodes;

namespace NexusPipeline.Modules.History;

internal static partial class RunRecordFormat
{
    private static void ValidateRecognitionReport(JsonObject report)
    {
        string[] allowed = ["schemaVersion", "semanticsVersion", "runId", "pluginId", "revision", "userId", "scriptInstanceId",
            "originalPlan", "lifecycleOutcome", "engineStatus", "attemptReports", "finalTaskResults", "incidents", "summary",
            "diagnostics", "evidenceLines", "hostEvidence", "displaySnapshot", "admissionBlocked", "verification"];
        Require(report.All(pair => allowed.Contains(pair.Key, StringComparer.Ordinal)));
        Require(report["verification"] is JsonObject && report["originalPlan"] is JsonObject);
        var verification = (JsonObject)report["verification"]!;
        var plan = (JsonObject)report["originalPlan"]!;
        Require(plan["protocolVersion"]?.GetValue<string>() == "0.3.0" && plan["semanticsVersion"]?.GetValue<string>() == "daily-flow-v2");
        ValidateRecognitionPlan(plan);
        ExactFields(verification, ["mode", "knownTaskCount", "opaqueTaskCount", "opaqueScopeCount", "unverifiedTaskIds",
            "opaqueScopeIds", "reasonCodes", "completion", "unknownRetainedOnRetry"]);
        Require(plan["tasks"] is JsonArray { Count: <= 1024 } && plan["verification"]?["opaqueScopes"] is JsonArray { Count: <= 1024 });
        var tasks = plan["tasks"]!.AsArray().Select(node => node!.AsObject()).ToArray();
        Require(tasks.All(task => task["recognition"]?.GetValue<string>() is "known" or "opaque"));
        var enabled = tasks.Where(task => task["enabled"]!.GetValue<bool>()).ToArray();
        string[] opaque = enabled.Where(task => task["recognition"]!.GetValue<string>() == "opaque").Select(task => task["id"]!.GetValue<string>()).ToArray();
        var scopes = plan["verification"]!["opaqueScopes"]!.AsArray().Where(node => node!["enabled"]!.GetValue<bool>()).ToArray();
        int known = enabled.Length - opaque.Length;
        Require(Count(verification, "knownTaskCount") == known && Count(verification, "opaqueTaskCount") == opaque.Length
            && Count(verification, "opaqueScopeCount") == scopes.Length);
        Require(Strings(verification["unverifiedTaskIds"]).SequenceEqual(opaque)
            && Strings(verification["opaqueScopeIds"]).SequenceEqual(scopes.Select(node => node!["id"]!.GetValue<string>())));
        Require(Strings(verification["reasonCodes"]).SequenceEqual(scopes.Select(node => node!["reasonCode"]!.GetValue<string>())
            .Concat(opaque.Length > 0 ? new[] { "tasks.not_individually_verified" } : []).Distinct(StringComparer.Ordinal)));
        Require(verification["mode"]?.GetValue<string>() == (opaque.Length + scopes.Length == 0 ? "complete" : known == 0 ? "opaque" : "partial"));
        Require(verification["unknownRetainedOnRetry"] is JsonValue flag && flag.TryGetValue<bool>(out _));
        ValidateCompletion(verification["completion"]);
        Require(report["attemptReports"] is JsonArray { Count: <= 1024 } && report["finalTaskResults"] is JsonArray { Count: <= 1024 });
        var taskIds = tasks.Select(task => task["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var opaqueIds = tasks.Where(task => task["recognition"]!.GetValue<string>() == "opaque").Select(task => task["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        int previousNumber = 0;
        foreach (var attempt in report["attemptReports"]!.AsArray())
        {
            Require(attempt is JsonObject);
            Require(attempt["number"] is JsonValue number && number.TryGetValue<int>(out int current) && current > previousNumber);
            previousNumber = attempt["number"]!.GetValue<int>();
            Require(attempt["attemptId"]?.GetValue<string>() == report["runId"]!.GetValue<string>() + ":" + previousNumber
                && Strings(attempt["selectedTaskIds"]).All(taskIds.Contains)
                && attempt["lifecycleOutcome"]?.GetValue<string>() is "running" or "completed" or "failed" or "cancelled");
            ValidateResults(attempt["taskResults"]);
            if (attempt["executionCompletion"] is { } completion) ValidateCompletion(completion);
        }
        ValidateResults(report["finalTaskResults"]);

        void ValidateResults(JsonNode? value)
        {
            Require(value is JsonArray { Count: <= 1024 });
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var result in value!.AsArray())
            {
                Require(result is JsonObject && result["taskId"] is JsonValue id && id.TryGetValue<string>(out _));
                string taskId = result!["taskId"]!.GetValue<string>();
                Require(taskIds.Contains(taskId) && seen.Add(taskId));
                if (opaqueIds.Contains(taskId))
                    Require(result["status"]?.GetValue<string>() is "pending" or "unknown" or "cancelled"
                        && result["evidence"] is JsonArray { Count: 0 });
            }
        }
    }

    private static void ValidateCompletion(JsonNode? node)
    {
        Require(node is JsonObject);
        var completion = (JsonObject)node!;
        ExactFields(completion, ["status", "basis", "exitCode"]);
        string? status = completion["status"]?.GetValue<string>(), basis = completion["basis"]?.GetValue<string>();
        int? code = completion["exitCode"]?.GetValue<int>();
        Require(status == "normal" && basis == "owned_worker_exit" && code == 0
            || status == "failed" && basis == "owned_process_nonzero" && code is not null and not 0
            || status == "unconfirmed" && basis == "unconfirmed_worker" && code is null);
    }

    private static int Count(JsonObject value, string field)
    {
        Require(value[field] is JsonValue item && item.TryGetValue<int>(out int count) && count is >= 0 and <= 1024);
        return value[field]!.GetValue<int>();
    }

    private static string[] Strings(JsonNode? value)
    {
        Require(value is JsonArray { Count: <= 1025 });
        var items = value!.AsArray().Select(node => node?.GetValue<string>()).ToArray();
        Require(items.All(item => item is { Length: > 0 and <= 2048 }) && items.Distinct(StringComparer.Ordinal).Count() == items.Length);
        return items!;
    }

    private static void ExactFields(JsonObject value, string[] fields)
        => Require(value.Count == fields.Length && fields.All(value.ContainsKey));
}
