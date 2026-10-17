using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Host.Composition;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Testing;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.Modules.Configuration.Exchange;

internal static partial class ConfigSwapSession
{
    /* ---------------- 会话与恢复入口 ---------------- */

    private static ConfigSwapRecovery? _recovery;

    /// <summary>
    /// 装配配置交换恢复：组合根在进程初始化时注入脚本/用户快照数据源；
    /// 恢复路径不再反向依赖 Host 组合根。未装配时调用恢复入口视为编程错误。
    /// </summary>
    public static void ConfigureRecovery(
        Func<string, ScriptInstance?> findScript,
        Func<IReadOnlyList<NexusUser>> snapshotUsers,
        Func<ConfigSessionMark, bool>? commitPendingInput = null)
    {
        _recovery = new ConfigSwapRecovery(findScript, snapshotUsers, commitPendingInput);
    }

    private static ConfigSwapRecovery Recovery
    {
        get
        {
            if (_recovery is null)
            {
                throw new InvalidOperationException("配置交换恢复未装配（请先调用 ConfigSwapSession.ConfigureRecovery）。");
            }
            return _recovery;
        }
    }

    /// <summary>操作前自愈：恢复逻辑由 ConfigSwapRecovery 统一负责。</summary>
    public static void RecoverIfNeeded(string scriptId, string userName, string configPath)
    {
        Recovery.RecoverIfNeeded(scriptId, userName, configPath);
    }

    /// <summary>启动恢复扫描：恢复逻辑由 ConfigSwapRecovery 统一负责。</summary>
    public static void RecoverInterrupted(IReadOnlyList<NexusUser>? users = null)
    {
        Recovery.RecoverInterrupted(users);
    }

    public static void StartRecoveryRetry()
    {
        Recovery.StartRecoveryRetry();
    }

    public static void StopRecoveryRetry()
    {
        Recovery.StopRecoveryRetry();
    }

    public static void DoRestore(string scriptId, string userName, ConfigSessionMark mark)
    {
        Recovery.DoRestore(scriptId, userName, mark);
    }

}
