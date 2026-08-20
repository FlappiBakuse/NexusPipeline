using System.Diagnostics;
using NexusPipeline.Models;

namespace NexusPipeline.Services;

/// <summary>编辑配置会话（WebServer 持有的进程句柄与标记）。</summary>
internal sealed class EditSession
{
    public required ScriptInstance Script { get; init; }

    public required ScriptUser User { get; init; }

    public Process? Process { get; set; }

    public ConfigSessionMark Mark { get; init; } = new();

    /// <summary>本次会话由宿主生成了配置模板（cancel 时需清理生成文件）。</summary>
    public bool GeneratedConfigTemplate { get; set; }
}
