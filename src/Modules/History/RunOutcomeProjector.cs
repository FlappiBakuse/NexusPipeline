using System.Text.Json.Nodes;

namespace NexusPipeline.Modules.History;

/// <summary>Projects current run facts without inferring business verification from engine success.</summary>
internal static class RunOutcomeProjector
{
    internal static RunOutcomeDimensions Project(RunRecord record, bool configPrepared, bool recoveryFailed)
    {
        JsonObject? report = record.TaskReport;
        var semantics = report is null ? (TaskReportSemantics?)null : RunRecordFormat.ReportSemantics(report);
        string engine = report?["engineStatus"]?.GetValue<string>() ?? record.Status switch
        {
            "success" => "succeeded",
            "failed" or "blocked" => "failed",
            "cancelled" => "cancelled",
            "running" => "running",
            _ => "unknown",
        };
        string execution = report?["lifecycleOutcome"]?.GetValue<string>() switch
        {
            "completed" => "completed",
            "failed" or "interrupted" => "failed",
            "cancelled" => "cancelled",
            "running" => "running",
            "not_started" => "not_started",
            _ => record.Status switch
            {
                "cancelled" => "cancelled",
                "success" or "partial" or "skipped" => "completed",
                "running" => "running",
                _ => record.Attempts == 0 ? "not_started" : "failed",
            },
        };
        string business = "unverified";
        JsonObject? counts = report?["summary"]?["counts"] as JsonObject;
        if (semantics == TaskReportSemantics.DailyFlow)
            business = report!["summary"]?["outcome"]?.GetValue<string>() switch
            {
                "failed" => "verified_failed", "partial" => "partial",
                "completed" => counts is not null && Count(counts, "succeeded") > 0 ? "verified_succeeded" : "satisfied",
                "no_tasks" => "inapplicable", "cancelled" => "cancelled", "blocked" => "blocked",
                _ => "pending",
            };
        // Authenticated engine events are not independent business verification.
        if (semantics == TaskReportSemantics.ProviderExecution) business = "unverified";
        string recovery = recoveryFailed ? "quarantined" : configPrepared ? "restored" : "not_required";
        return new(engine, business, execution, recovery);
    }

    private static int Count(JsonObject counts, string key) =>
        counts[key] is JsonValue value && value.TryGetValue<int>(out int result) && result >= 0 ? result : 0;
}
