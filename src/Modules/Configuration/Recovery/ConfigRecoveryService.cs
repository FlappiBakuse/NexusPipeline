using System.Collections.Concurrent;
using System.Text;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Users;
using NexusPipeline.Platform.Storage;
namespace NexusPipeline.Modules.Configuration.Recovery;


/// <summary>
/// 配置储存管理对外门面（拆分）：保持全部外部 API 签名不变。
/// 实现分层：文件原语 <see cref="ConfigSwapPrimitives"/>（安全移动/原子替换/重试/跨进程互斥）、
/// 会话与恢复 <see cref="ConfigSwapSession"/>（.session 标记/门禁/回滚/finally 还原/启动扫描恢复）、
/// 数据目录 <see cref="ConfigPaths"/>。数据保全序：original（原配置）&gt; config &gt; store（可重建）。
/// </summary>
internal static class ConfigRecoveryService
{
    /// <summary>Read-only projection of the existing recovery journals for execution admission.</summary>
    internal static IReadOnlyList<ConfigRecoveryIsolationProjection> SnapshotIsolation()
    {
        var result = new List<ConfigRecoveryIsolationProjection>();
        string dataRoot = AppPaths.DataDir;
        if (!Directory.Exists(dataRoot))
        {
            if (ConfigUpdateAdmission.HasPendingRecovery(dataRoot))
                result.Add(ConfigRecoveryIsolationProjection.Unknown("data-root-unavailable"));
            return result;
        }
        try
        {
            foreach (string scriptDir in Directory.EnumerateDirectories(dataRoot))
            {
                if ((File.GetAttributes(scriptDir) & FileAttributes.ReparsePoint) != 0)
                {
                    result.Add(ConfigRecoveryIsolationProjection.Unknown("reparse-point"));
                    continue;
                }
                string scriptWork = Path.Combine(scriptDir, ConfigPaths.WorkDirName);
                if (Directory.Exists(scriptWork)
                    && ((File.GetAttributes(scriptWork) & FileAttributes.ReparsePoint) != 0
                        || Directory.EnumerateFileSystemEntries(scriptWork)
                            .Any(path => !Path.GetFileName(path).Equals("script", StringComparison.OrdinalIgnoreCase))))
                    result.Add(ConfigRecoveryIsolationProjection.Unknown("script-work-residue"));
                foreach (string userDir in Directory.EnumerateDirectories(scriptDir))
                {
                    if (Path.GetFileName(userDir).Equals(ConfigPaths.WorkDirName, StringComparison.OrdinalIgnoreCase)) continue;
                    if ((File.GetAttributes(userDir) & FileAttributes.ReparsePoint) != 0)
                    {
                        result.Add(ConfigRecoveryIsolationProjection.Unknown("reparse-point"));
                        continue;
                    }
                    string scriptId = Path.GetFileName(scriptDir);
                    string userId = Path.GetFileName(userDir);
                    bool hasMark = ConfigUpdateAdmission.ExistsOrThrow(Path.Combine(userDir, ".session"))
                        || ConfigUpdateAdmission.ExistsOrThrow(Path.Combine(userDir, ".session.bak"));
                    if (!hasMark)
                    {
                        string work = Path.Combine(userDir, ConfigPaths.WorkDirName);
                        if (Directory.Exists(work)
                            && ((File.GetAttributes(work) & FileAttributes.ReparsePoint) != 0
                                || Directory.EnumerateFileSystemEntries(work)
                                    .Any(path => !Path.GetFileName(path).Equals("script", StringComparison.OrdinalIgnoreCase))))
                            result.Add(ConfigRecoveryIsolationProjection.Unknown("unowned-work"));
                        continue;
                    }
                    ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userId);
                    result.Add(mark is null
                        ? ConfigRecoveryIsolationProjection.Unknown("unreadable-journal")
                        : ConfigRecoveryIsolationProjection.From(mark));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Add(ConfigRecoveryIsolationProjection.Unknown("scan-unavailable"));
        }
        return result;
    }

    /* ---------------- 恢复（转发 ConfigSwapSession；运行期替换/同步/重试由 ConfigRunSession 直达 ConfigSwapSession） ---------------- */

    /// <summary>启动恢复：按当前全局用户绑定的 UserId 白名单处理会话标记与配置替换，并保留脚本级现场。</summary>
    public static void RecoverInterrupted(IReadOnlyList<NexusUser>? users = null)
    {
        ConfigSwapSession.RecoverInterrupted(users);
    }

    /// <summary>启动后台恢复重试循环：每 10 秒尝试还原待办项（孤儿进程退出/文件解锁后自动完成），直至全部成功或进程退出。</summary>
    public static void StartRecoveryRetry()
    {
        ConfigSwapSession.StartRecoveryRetry();
    }

    public static void StopRecoveryRetry()
    {
        ConfigSwapSession.StopRecoveryRetry();
    }
}

internal sealed record ConfigRecoveryIsolationProjection(
    string RecoveryId,
    string ScriptId,
    string UserId,
    string ConfigPath,
    IReadOnlyList<string> ExtraConfigPaths,
    string WorkingDirectory,
    string WritableRoot,
    string CauseCode,
    string ScopeQuality,
    bool MayContinueIndependent,
    string OriginExecutionId,
    bool ExplicitIsolation)
{
    public static ConfigRecoveryIsolationProjection Unknown(string cause) =>
        new("", "", "", "", Array.Empty<string>(), "", "", cause, "unavailable", false, "", false);

    public static ConfigRecoveryIsolationProjection From(ConfigSessionMark mark)
    {
        ConfigSessionRecoveryIsolation? isolation = mark.RecoveryIsolation;
        return new(isolation?.RecoveryId ?? "", mark.ScriptId, mark.UserId, mark.ConfigPath,
            mark.ExtraConfigPaths.Select(path => path.Path).ToArray(), mark.WorkingDirectory, mark.WritableRoot,
            isolation?.CauseCode ?? "legacy-recovery-journal",
            isolation?.ScopeQuality ?? "unavailable",
            isolation?.MayContinueIndependent == true,
            mark.OriginExecutionId, isolation is not null);
    }
}
