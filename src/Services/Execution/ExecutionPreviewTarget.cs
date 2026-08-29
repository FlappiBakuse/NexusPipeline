using NexusPipeline.Services;

namespace NexusPipeline.Services.Execution;

internal enum ExecutionPreviewSource
{
    None,
    Pc,
    Emulator,
}

internal enum ExecutionPreviewState
{
    Waiting,
    Ready,
    Unavailable,
}

/// <summary>一次运行当前允许预览的游戏目标。目标来自宿主执行状态，绝不接受浏览器提交的进程或窗口参数。</summary>
internal sealed record ExecutionPreviewTarget(
    string ScriptId,
    string ScriptName,
    ExecutionPreviewSource Source,
    ExecutionPreviewState State,
    int? ProcessId = null,
    IEmulatorDriver? EmulatorDriver = null,
    string? Error = null);
