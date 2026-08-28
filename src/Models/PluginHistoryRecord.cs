namespace NexusPipeline.Models;

/// <summary>运行历史中持久化的插件展示快照；只包含已校验的纯文本字段。</summary>
public sealed class PluginHistoryRecord
{
    public string PluginName { get; set; } = "";

    public string PluginDisplayName { get; set; } = "";

    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public int Order { get; set; }

    public List<PluginHistoryBadgeRecord> Badges { get; set; } = new();

    public List<PluginHistoryFieldRecord> Fields { get; set; } = new();
}

public sealed class PluginHistoryBadgeRecord
{
    public string Label { get; set; } = "";

    public string Tone { get; set; } = "muted";

    public string Title { get; set; } = "";
}

public sealed class PluginHistoryFieldRecord
{
    public string Label { get; set; } = "";

    public string Value { get; set; } = "";

    public string Tone { get; set; } = "muted";
}
