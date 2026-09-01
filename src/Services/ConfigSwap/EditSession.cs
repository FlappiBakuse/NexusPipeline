using System.Diagnostics;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;

namespace NexusPipeline.Services;

/// <summary>编辑配置会话（WebServer 持有的进程句柄与标记）。</summary>
internal sealed class EditSession
{
    public required ScriptInstance Script { get; init; }

    public required ResolvedScriptUser User { get; init; }

    public Process? Process { get; set; }

    public ConfigSessionMark Mark { get; init; } = new();
}
