using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;

namespace NexusPipeline.Services;

/// <summary>统一判断专项脚本实例是否仍能找到并使用其绑定的专项插件。</summary>
internal static class PluginAvailability
{
    public static string? GetUnavailableReason(
        ScriptInstance script,
        IPluginAvailability plugins)
    {
        string pluginType = script.PluginType?.Trim() ?? "";
        if (pluginType.Length == 0)
        {
            return null;
        }

        string? reason = GetUnavailableReason(pluginType, plugins);
        return reason is null
            ? null
            : $"脚本实例「{script.Name}」绑定的{reason}";
    }

    public static string? GetUnavailableReason(
        string pluginType,
        IPluginAvailability plugins)
    {
        string name = pluginType?.Trim() ?? "";
        if (name.Length == 0)
        {
            return null;
        }

        if (!plugins.IsKnownPlugin(name) || !plugins.IsDataSpecializedPlugin(name))
        {
            return $"专项插件「{name}」未安装，请先安装对应专项插件";
        }
        if (!plugins.IsEnabled(name))
        {
            return $"专项插件「{name}」当前不可用，请先启用对应专项插件";
        }
        return null;
    }
}
