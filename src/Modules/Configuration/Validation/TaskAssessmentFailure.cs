namespace NexusPipeline.Modules.Configuration.Validation;

/// <summary>Stable feedback categories; raw plugin/IO exception text is never user-facing.</summary>
internal static class TaskAssessmentFailure
{
    internal static string Code(string message) => message switch
    {
        var value when value.StartsWith("configuration_conflict", StringComparison.Ordinal) => "configuration_busy",
        var value when value.StartsWith("task_protocol_timeout", StringComparison.Ordinal) => "timeout",
        var value when value.StartsWith("resource_limit", StringComparison.Ordinal) => "resource_limit",
        var value when value.StartsWith("config_unavailable", StringComparison.Ordinal) => "config_unavailable",
        _ => "protocol_error",
    };

    internal static string SafeMessage(string message, string locale) => (Code(message), locale == "zh-CN") switch
    {
        ("timeout", true) => "配置检查超时，保存结果已保留，请重新读取计划。",
        ("configuration_busy", true) => "配置正在变化，保存结果已保留，请重新读取计划。",
        ("config_unavailable", true) => "本绑定的配置快照不可用，保存结果已保留。",
        ("resource_limit", true) => "配置检查超出资源预算，保存结果已保留。",
        (_, true) => "插件配置检查未能完成，保存结果已保留。",
        ("timeout", false) => "Configuration check timed out. Your save was kept; refresh the plan.",
        ("configuration_busy", false) => "Configuration changed during checking. Your save was kept; refresh the plan.",
        ("config_unavailable", false) => "This binding's configuration snapshot is unavailable. Your save was kept.",
        ("resource_limit", false) => "Configuration check exceeded its resource budget. Your save was kept.",
        _ => "Plugin configuration check could not complete. Your save was kept.",
    };
}
