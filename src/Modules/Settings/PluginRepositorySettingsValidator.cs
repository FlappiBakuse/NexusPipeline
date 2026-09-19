using System.Text.Json;

namespace NexusPipeline.Modules.Settings;

/// <summary>解析并校验插件仓库来源通道；安装归属、pending 和 ownership 由 Plugins.Repository 负责。</summary>
internal static class PluginRepositorySettingsValidator
{
    internal static string ReadChannel(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("pluginRepository", out JsonElement repository))
        {
            return "stable";
        }
        if (repository.ValueKind != JsonValueKind.Object)
        {
            throw new PluginRepositoryConfigurationException("settings.json 的 pluginRepository 必须是对象");
        }
        if (!repository.TryGetProperty("channel", out JsonElement channel))
        {
            return "stable";
        }
        if (channel.ValueKind != JsonValueKind.String)
        {
            throw new PluginRepositoryConfigurationException("settings.json 的 pluginRepository.channel 必须是字符串");
        }
        string value = channel.GetString() ?? "";
        if (value is not ("stable" or "develop"))
        {
            throw new PluginRepositoryConfigurationException(
                $"settings.json 的 pluginRepository.channel 无效：{value}");
        }
        return value;
    }

    internal static void Normalize(PluginRepositorySettings? settings)
    {
        if (settings is null)
        {
            throw new PluginRepositoryConfigurationException("settings.json 的 pluginRepository 不能为 null");
        }
        if (settings.Channel is not ("stable" or "develop"))
        {
            throw new PluginRepositoryConfigurationException(
                $"settings.json 的 pluginRepository.channel 无效：{settings.Channel}");
        }
    }
}

internal sealed class PluginRepositoryConfigurationException : Exception
{
    internal PluginRepositoryConfigurationException(string message) : base(message)
    {
    }
}
