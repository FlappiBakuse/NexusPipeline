using System.Text.Json.Nodes;

namespace NexusPipeline.Modules.History;

/// <summary>Projects new run facts without rewriting or inferring verification for old history.</summary>
internal static class RunOutcomeProjector
{
    internal static RunOutcomeDimensions Project(RunRecord record, bool configPrepared, bool recoveryFailed)
    {
        JsonObject? report = record.TaskReport;
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
        if (counts is not null)
        {
            int total = Count(counts, "total");
            int succeeded = Count(counts, "succeeded");
            int failed = Count(counts, "failed") + Count(counts, "partial");
            int skipped = Count(counts, "skipped");
            if (failed > 0) business = "verified_failed";
            else if (total > 0 && succeeded == total) business = "verified_succeeded";
            else if (total > 0 && skipped == total) business = "satisfied";
            else if (total == 0) business = "inapplicable";
        }
        // Authenticated engine events are not independent business verification.
        if (report?["businessVerification"]?.GetValue<string>() == "unverified") business = "unverified";
        string recovery = recoveryFailed ? "quarantined" : configPrepared ? "restored" : "not_required";
        return new(engine, business, execution, recovery);
    }

    private static int Count(JsonObject counts, string key) =>
        counts[key] is JsonValue value && value.TryGetValue<int>(out int result) && result >= 0 ? result : 0;
}
