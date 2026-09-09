using System.Text.RegularExpressions;
using NexusPipeline.Models;

namespace NexusPipeline.Localization;

/// <summary>把持久化运行结果码投影为通知等宿主输出使用的语言文本。</summary>
internal static class RunResultLocalization
{
    public static string Detail(RunRecord record, string? locale = null)
    {
        string fallback = record.ResultDetail ?? "";
        string normalized = LocaleCatalog.Normalize(locale ?? LocaleContext.Current);
        if (normalized == LocaleCatalog.DefaultLocale)
        {
            return fallback;
        }

        string reason = GetArg(record.ResultArgs, "reason", fallback);
        return record.ResultCode switch
        {
            "run.running" => "Running",
            "run.cancelled" when fallback == "运行已取消" => "Run cancelled",
            "run.daily_cap" => $"The daily success limit was reached ({GetArg(record.ResultArgs, "successful", "0")}/{GetArg(record.ResultArgs, "maximum", "0")}); this run was skipped",
            "run.user_unavailable" => $"User \"{GetArg(record.ResultArgs, "user", "")}\" does not exist or is disabled",
            "run.config_selection_required" => "Multiple configurations exist in the script directory. Choose one in Edit configuration before running",
            "run.user_config_load_failed" => $"Failed to load the user's configuration: {reason}",
            "run.retry_prepare_failed" => $"Configuration swap before retry failed: {reason}",
            "run.max_attempts" => $"The maximum number of attempts ({GetArg(record.ResultArgs, "maximum", record.MaxAttempts.ToString())}) failed; last reason: {reason}",
            "run.script_missing" => "The script instance does not exist or was deleted",
            "run.no_enabled_users" => "No enabled users are configured for this script instance; skipped",
            "run.plugin_unavailable" => $"The bound {TranslatePluginReason(reason)}; this run was skipped",
            "run.success" when fallback == "一次成功" => "Succeeded on the first attempt",
            _ when TryGetAttemptSuccess(fallback, out int attempt) => $"Succeeded on attempt {attempt}",
            "run.partial" when fallback == "判断脚本判定部分完成" => "The judge reported partial completion",
            _ => fallback,
        };
    }

    private static string GetArg(IReadOnlyDictionary<string, string> args, string key, string fallback)
    {
        return args.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    private static string TranslatePluginReason(string reason)
    {
        Match missing = Regex.Match(reason, "^专项插件「(?<name>.+)」未安装");
        if (missing.Success)
        {
            return $"specialized plugin \"{missing.Groups["name"].Value}\" is not installed";
        }
        Match disabled = Regex.Match(reason, "^专项插件「(?<name>.+)」当前不可用");
        if (disabled.Success)
        {
            return $"specialized plugin \"{disabled.Groups["name"].Value}\" is unavailable";
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
