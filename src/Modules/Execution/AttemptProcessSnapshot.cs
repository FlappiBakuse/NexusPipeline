using NexusPipeline.Platform.Processes;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Modules.Execution;

/// <summary>
/// 一次 Attempt 监控 tick 的进程视图。快照只活过当前 tick，避免脚本退出检测和游戏窗口查找
/// 分别枚举系统进程，也避免把 Process wrapper 的生命周期带进常驻缓存。
/// </summary>
internal sealed class AttemptProcessSnapshot
{
    private readonly IReadOnlyDictionary<int, ProcessTree.ProcessNode> _processes;

    private AttemptProcessSnapshot(IReadOnlyDictionary<int, ProcessTree.ProcessNode> processes)
    {
        _processes = processes;
    }

    internal static AttemptProcessSnapshot? Capture()
    {
        try
        {
            return new AttemptProcessSnapshot(ProcessTree.SnapshotProcesses());
        }
        catch (Exception ex)
        {
            // 进程快照只负责减少 steady tick 的重复枚举；能力失败时保留原有按名检测回退。
            Logger.Debug($"[进程快照] 当前监控 tick 获取系统进程快照失败，将按原路径回退：{ex.Message}");
            return null;
        }
    }

    internal bool IsExecutableRunning(string executablePath)
    {
        string baseName = Path.GetFileNameWithoutExtension(executablePath);
        return baseName.Length > 0 && _processes.Values.Any(node => ProcessTree.IsSameProcessName(node.ExeName, baseName));
    }

    internal IEnumerable<int> FindProcessIds(string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return Array.Empty<int>();
        }
        return _processes.Values
            .Where(node => ProcessTree.IsSameProcessName(node.ExeName, baseName))
            .Select(node => node.Pid)
            .ToArray();
    }
}
