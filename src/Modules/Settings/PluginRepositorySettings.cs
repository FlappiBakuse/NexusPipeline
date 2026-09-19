using System.Text.Json.Serialization;

namespace NexusPipeline.Modules.Settings;

/// <summary>官方插件仓库来源通道。只允许启动时选择 stable 或 develop。</summary>
public sealed class PluginRepositorySettings
{
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "stable";
}
