using System.Globalization;
using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Persistence;

/// <summary>
/// 负责 service-owned 运行状态的目录布局与一次性迁移。
/// 调用方必须已经取得单实例互斥体后再执行 <see cref="EnsureMigrated"/>。
/// </summary>
internal sealed class RuntimeStateLayout
{
    public RuntimeStateLayout(string appRoot)
    {
        AppRoot = Path.GetFullPath(appRoot);
        InternalDir = Path.Combine(AppRoot, ".nxp");
        RuntimeDir = Path.Combine(InternalDir, "runtime");
        StateDir = Path.Combine(InternalDir, "state");
        RecoveryDir = Path.Combine(StateDir, "recovery");
        ServicePidPath = Path.Combine(RuntimeDir, "service.pid");
        WebPortPath = Path.Combine(RuntimeDir, "web.port");
        SchedulerStatePath = Path.Combine(StateDir, "scheduler-state.json");
        LegacyServicePidPath = Path.Combine(AppRoot, "service.pid");
        LegacyWebPortPath = Path.Combine(AppRoot, "web.port");
        LegacySchedulerStatePath = Path.Combine(AppRoot, "scheduler-state.json");
    }

    public string AppRoot { get; }

    public string InternalDir { get; }

    public string RuntimeDir { get; }

    public string StateDir { get; }

    public string RecoveryDir { get; }

    public string ServicePidPath { get; }

    public string WebPortPath { get; }

    public string SchedulerStatePath { get; }

    public string LegacyServicePidPath { get; }

    public string LegacyWebPortPath { get; }

    public string LegacySchedulerStatePath { get; }

    public void EnsureMigrated()
    {
        EnsureDirectories();
        MigrateSchedulerState();
        RemoveLegacyMarker(LegacyServicePidPath, "service.pid");
        RemoveLegacyMarker(LegacyWebPortPath, "web.port");
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RuntimeDir);
        Directory.CreateDirectory(StateDir);
    }

    public int? ReadLegacyWebPort()
    {
        try
        {
            if (!File.Exists(LegacyWebPortPath))
            {
                return null;
            }
            string text = File.ReadAllText(LegacyWebPortPath).Trim();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
                && port is >= 1024 and <= 65535
                ? port
                : null;
        }
        catch (Exception ex)
        {
            Logger.Debug($"读取旧 Web 端口标记失败：{ex.Message}");
            return null;
        }
    }

    private void MigrateSchedulerState()
    {
        if (!File.Exists(LegacySchedulerStatePath))
        {
            return;
        }

        if (!File.Exists(SchedulerStatePath))
        {
            try
            {
                // 同一安装目录内的 File.Move 是原子 rename；迁移中断时旧文件仍保留，
                // 下一次获得 service ownership 后可以继续重试。
                File.Move(LegacySchedulerStatePath, SchedulerStatePath);
                Audit.Log(Audit.System, "迁移调度运行状态", "scheduler-state.json → .nxp\\state\\scheduler-state.json");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[运行时] 迁移 scheduler-state.json 失败，将在下次启动重试：{ex.Message}");
            }
            return;
        }

        string recoveryPath = CreateRecoveryPath();
        try
        {
            // 新路径已经是权威状态；旧文件另存为 recovery，绝不静默覆盖任一份数据。
            File.Move(LegacySchedulerStatePath, recoveryPath);
            Logger.Warn($"[运行时] 检测到新旧 scheduler-state.json 冲突，新状态保持有效，旧状态已保留：{recoveryPath}");
            Audit.Log(Audit.System, "保留冲突调度运行状态", Path.GetFileName(recoveryPath));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[运行时] 保存冲突 scheduler-state.json 失败，旧文件仍保留待下次启动处理：{ex.Message}");
        }
    }

    private string CreateRecoveryPath()
    {
        Directory.CreateDirectory(RecoveryDir);
        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        string path = Path.Combine(RecoveryDir, $"scheduler-state.legacy-conflict-{stamp}.json");
        return File.Exists(path)
            ? Path.Combine(RecoveryDir, $"scheduler-state.legacy-conflict-{stamp}-{Guid.NewGuid():N}.json")
            : path;
    }

    private static void RemoveLegacyMarker(string path, string name)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }
            File.Delete(path);
            Audit.Log(Audit.System, "清理旧运行标记", name);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[运行时] 清理旧 {name} 标记失败，将在下次启动重试：{ex.Message}");
        }
    }
}
