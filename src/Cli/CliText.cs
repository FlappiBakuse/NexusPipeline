using NexusPipeline.Localization;

namespace NexusPipeline.Cli;

/// <summary>CLI 可见文案的稳定资源入口。key 只描述用途，fallback 仅用于资源不可用时的安全降级。</summary>
internal static class CliText
{
    public static string Resource(string name)
    {
        return name switch
        {
            "script" => Get("resource.script", "脚本实例"),
            "queue" => Get("resource.queue", "调度队列"),
            "user" => Get("resource.user", "用户"),
            "plugin" => Get("resource.plugin", "插件"),
            "binding" => Get("resource.binding", "绑定"),
            _ => name,
        };
    }

    public static string Label(string fallback)
    {
        return fallback switch
        {
            "脚本实例" => Get("resource.script", fallback),
            "调度队列" => Get("resource.queue", fallback),
            "用户" => Get("resource.user", fallback),
            "插件" => Get("resource.plugin", fallback),
            "用户绑定" => Get("resource.binding", fallback),
            "脚本 ID 或名称" => Get("label.script_reference", fallback),
            "队列 ID 或名称" => Get("label.queue_reference", fallback),
            "用户 ID 或名称" => Get("label.user_reference", fallback),
            "插件名称" => Get("label.plugin_name", fallback),
            "专用插件标识" => Get("label.plugin_identifier", fallback),
            "脚本根目录" => Get("label.script_root", fallback),
            "运行 ID" => Get("label.run_id", fallback),
            "历史记录 ID" => Get("label.history_id", fallback),
            "配置操作" => Get("label.config_action", fallback),
            "用户名" => Get("label.username", fallback),
            "删除确认用户名" => Get("label.confirm_username", fallback),
            "密钥字段名" => Get("label.secret_key", fallback),
            "密钥值" => Get("label.secret_value", fallback),
            "JSON 文件路径（或 -）" => Get("label.json_file", fallback),
            "文件路径" => Get("label.file_path", fallback),
            _ => fallback,
        };
    }

    public static string Get(
        string key,
        string fallback,
        params (string Name, object? Value)[] values)
    {
        var args = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string name, object? value) in values)
        {
            args[name] = value;
        }
        return HostLocalization.TranslateNamed(
            "cli." + key,
            fallback,
            args,
            LocaleCatalog.HostLocale);
    }
}
