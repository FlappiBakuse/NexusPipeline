using System.Text.RegularExpressions;
using NexusPipeline.Models;

namespace NexusPipeline.Localization;

/// <summary>把持久化运行结果码投影为通知等宿主输出使用的语言文本。</summary>
internal static class RunResultLocalization
{
    public static string Detail(RunRecord record, string? locale = null)
    {
        string fallback = record.ResultDetail ?? "";
        string reason = GetArg(record.ResultArgs, "reason", fallback);
        var args = record.ResultArgs.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal);
        return record.ResultCode switch
        {
            "run.running" => HostLocalization.TranslateNamed("run.running", "运行中", args, locale),
            "run.cancelled" when fallback == "运行已取消" => HostLocalization.TranslateNamed("run.cancelled", fallback, args, locale),
            "run.daily_cap" => HostLocalization.TranslateNamed("run.daily_cap", fallback, args, locale),
            "run.user_unavailable" => HostLocalization.TranslateNamed("run.user_unavailable", fallback, args, locale),
            "run.config_selection_required" => HostLocalization.TranslateNamed("run.config_selection_required", fallback, args, locale),
            "run.user_config_load_failed" => HostLocalization.TranslateNamed("run.user_config_load_failed", fallback, args, locale),
            "run.retry_prepare_failed" => HostLocalization.TranslateNamed("run.retry_prepare_failed", fallback, args, locale),
            "run.max_attempts" => HostLocalization.TranslateNamed("run.max_attempts", fallback, args, locale),
            "run.script_missing" => HostLocalization.TranslateNamed("run.script_missing", fallback, args, locale),
            "run.no_enabled_users" => HostLocalization.TranslateNamed("run.no_enabled_users", fallback, args, locale),
            "run.plugin_unavailable" => HostLocalization.TranslateNamed(
                "run.plugin_unavailable",
                fallback,
                new Dictionary<string, object?>
                {
                    ["reason"] = TranslatePluginReason(reason, locale),
                },
                locale),
            "run.success" when fallback == "一次成功" => HostLocalization.TranslateNamed("run.success_first", fallback, args, locale),
            _ when TryGetAttemptSuccess(fallback, out int attempt) => HostLocalization.TranslateNamed(
                "run.success_attempt",
                fallback,
                new Dictionary<string, object?> { ["attempt"] = attempt },
                locale),
            "run.partial" when fallback == "判断脚本判定部分完成" => HostLocalization.TranslateNamed("run.partial_judge", fallback, args, locale),
            _ => fallback,
        };
    }

    private static string GetArg(IReadOnlyDictionary<string, string> args, string key, string fallback)
    {
        return args.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    private static string TranslatePluginReason(string reason, string? locale)
    {
        Match missing = Regex.Match(reason, "^专项插件「(?<name>.+)」未安装");
        if (missing.Success)
        {
            return HostLocalization.TranslateNamed(
                "run.plugin_missing",
                $"专项插件「{missing.Groups["name"].Value}」未安装",
                new Dictionary<string, object?> { ["name"] = missing.Groups["name"].Value },
                locale);
        }
        Match disabled = Regex.Match(reason, "^专项插件「(?<name>.+)」当前不可用");
        if (disabled.Success)
        {
            return HostLocalization.TranslateNamed(
                "run.plugin_disabled",
                $"专项插件「{disabled.Groups["name"].Value}」当前不可用",
                new Dictionary<string, object?> { ["name"] = disabled.Groups["name"].Value },
                locale);
        }
        return reason;
    }

    private static bool TryGetAttemptSuccess(string value, out int attempt)
    {
        attempt = 0;
        Match match = Regex.Match(value, "^第 (?<attempt>\\d+) 次尝试成功$");
        return match.Success && int.TryParse(match.Groups["attempt"].Value, out attempt);
    }
}
