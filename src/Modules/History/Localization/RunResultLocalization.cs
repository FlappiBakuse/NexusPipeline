using NexusPipeline.Shared.Localization;

namespace NexusPipeline.Modules.History.Localization;

internal static class RunResultLocalization
{
    public static string Detail(RunRecord record, string? locale = null)
    {
        string fallback = record.ResultDetail ?? "";
        var args = record.ResultArgs.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal);
        if (record.ResultCode == "run.success")
        {
            args["attempt"] = record.Attempts;
            return HostLocalization.TranslateNamed(record.Attempts == 1 ? "run.success_first" : "run.success_attempt", fallback, args, locale);
        }
        if (record.ResultCode == "tasks_unverified"
            && record.TaskReport?["summary"]?["counts"]?["unknown"]?.GetValue<int>() is int unknown && unknown > 0)
            return HostLocalization.TranslateNamed("tasks.outcome.unverified", fallback,
                new Dictionary<string, object?> { ["count"] = unknown }, locale);
        return string.IsNullOrEmpty(record.ResultCode) ? fallback
            : HostLocalization.TranslateNamed(record.ResultCode, fallback, args, locale);
    }
}
