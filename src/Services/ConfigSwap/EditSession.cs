using System.Diagnostics;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Services.Execution;

namespace NexusPipeline.Services;

/// <summary>编辑配置会话（WebServer 持有的进程句柄与标记）。</summary>
internal sealed class EditSession
{
    public required ScriptInstance Script { get; init; }

    public required ResolvedScriptUser User { get; init; }

    /// <summary>编辑开始时冻结的有效 profile；提交校验沿用同一版本，避免与当前插件重新加载的 validator 混用。</summary>
    public ResolvedScriptSpec? Spec { get; init; }

    public Process? Process { get; set; }

    public ConfigSessionMark Mark { get; init; } = new();
}
