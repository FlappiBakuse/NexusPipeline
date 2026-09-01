using System.Collections.Concurrent;
using System.Text;
using NexusPipeline.Extensibility;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>
/// 配置储存管理对外门面（拆分）：保持全部外部 API 签名不变。
/// 实现分层：文件原语 <see cref="ConfigSwapPrimitives"/>（安全移动/原子替换/重试/跨进程互斥）、
/// 会话与恢复 <see cref="ConfigSwapSession"/>（.session 标记/门禁/回滚/finally 还原/启动扫描恢复）、
/// 数据目录 <see cref="ConfigSwapPaths"/>。数据保全序：original（原配置）&gt; config &gt; store（可重建）。
/// </summary>
internal static class UserConfigManager
{
    public static readonly ConcurrentDictionary<string, EditSession> EditSessions = new();

    /* ---------------- 数据目录（转发 ConfigSwapPaths） ---------------- */

    public static string UserDir(string scriptId, string userName)
    {
        return ConfigSwapPaths.UserDir(scriptId, userName);
    }

    public static string StoreDir(string scriptId, string userName)
    {
        return ConfigSwapPaths.StoreDir(scriptId, userName);
    }

    public static string CacheDir(string scriptId, string userName)
    {
        return ConfigSwapPaths.CacheDir(scriptId, userName);
    }

    public static string RetryStoreDir(string scriptId, string userName)
    {
        return ConfigSwapPaths.RetryStoreDir(scriptId, userName);
    }

    /// <summary>判断脚本专用目录（可读写）；无用户时兜底 data/{脚本Id}/script。</summary>
    public static string ScriptDir(string scriptId, string? userName)
    {
        return ConfigSwapPaths.ScriptDir(scriptId, userName);
    }

    /// <summary>配置替换备份目录（无用户交换时用于还原；有用户时由配置交换机制还原，备份作双保险）。</summary>
    public static string ReplaceBackupDir(string scriptId, string? userName)
    {
        return ConfigSwapPaths.ReplaceBackupDir(scriptId, userName);
    }

    /// <summary>准备判断脚本目录：清空重建（运行开始调用）。</summary>
    public static void PrepareScriptDir(string scriptId, string? userName)
    {
        ConfigSwapPaths.PrepareScriptDir(scriptId, userName);
    }

    /// <summary>运行结束清理：清空判断脚本目录与配置替换备份目录。</summary>
    public static void CleanupScriptArea(string scriptId, string? userName)
    {
        ConfigSwapPaths.CleanupScriptArea(scriptId, userName);
    }

    /* ---------------- 对外操作 ---------------- */

    /// <summary>判断用户在脚本实例上是否已有配置快照（store 目录存在且非空）；首次编辑配置以此为准。</summary>
    public static bool HasSnapshot(string scriptId, string userName)
    {
        string store = StoreDir(scriptId, userName);
        return Directory.Exists(store) && Directory.EnumerateFileSystemEntries(store).Any();
    }

    /// <summary>运行前准备：config → original（移动），store → config（复制）。失败自动回滚并还原现场。
    /// v0.12.8 起绑定不再建立快照：快照为空且现场配置存在时，先把现场配置复制为初始快照（复用语义），再执行交换。</summary>
    public static bool PrepareForRun(string scriptId, string userName, string configPath, out string? error)
    {
        error = null;
        bool prepared = false;
        try
        {
            ConfigSwapPrimitives.WithSwapLock(scriptId, () =>
            {
                ConfigSwapSession.RecoverIfNeeded(scriptId, userName, configPath);
                string store = StoreDir(scriptId, userName);
                if (!Directory.Exists(store) || !Directory.EnumerateFileSystemEntries(store).Any())
                {
                    if (PathKindUtil.KindOf(configPath) != PathKind.Missing)
                    {
                        ConfigSwapPrimitives.ClearPath(store, PathKindUtil.KindOf(store));
                        ConfigSwapPrimitives.CopyAs(configPath, store, PathKind.Dir);
                        Audit.Log(Audit.System, "运行前建立初始配置快照", $"脚本 {scriptId} / 用户 {userName}：{configPath} → {store}");
                    }
                }
                var mark = new ConfigSessionMark
                {
                    ScriptId = scriptId,
                    UserName = userName,
                    ConfigPath = configPath,
                    OriginalKind = PathKindUtil.Text(PathKindUtil.KindOf(configPath)),
                    Phase = "run",
                };
                string cache = CacheDir(scriptId, userName);
                string retryStore = RetryStoreDir(scriptId, userName);
                // 标记先行：任何时刻崩溃（含移动配置前后）都可恢复——original 空时恢复仅清标记（现场未动），original 有内容时完整还原。
                mark.Write();
                ConfigSwapPrimitives.ClearPath(cache, PathKindUtil.KindOf(cache));
                ConfigSwapPrimitives.ClearPath(retryStore, PathKindUtil.KindOf(retryStore));
                ConfigSwapPrimitives.MoveAs(configPath, cache, PathKind.Dir);
                if (Directory.Exists(store) && Directory.EnumerateFileSystemEntries(store).Any())
                {
                    ConfigSwapPrimitives.CopyAs(store, configPath, ConfigSwapPrimitives.RestoreKind(mark));
                    ConfigSwapPrimitives.CopyAs(store, retryStore, PathKind.Dir);
                }
                else if (PathKindUtil.Parse(mark.OriginalKind) == PathKind.Dir)
                {
                    Directory.CreateDirectory(configPath);
                }
                prepared = true;
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try
            {
                ConfigSwapPrimitives.WithSwapLock(scriptId, () =>
                {
                    if (prepared)
                    {
                        ConfigSwapSession.DoRestore(scriptId, userName, ConfigSessionMark.TryRead(scriptId, userName) ?? new ConfigSessionMark
                        {
                            ScriptId = scriptId,
                            UserName = userName,
                            ConfigPath = configPath,
                            OriginalKind = PathKindUtil.Text(PathKindUtil.KindOf(configPath)),
                        });
                    }
                    else
                    {
                        string cache = CacheDir(scriptId, userName);
                        if (Directory.Exists(cache) && Directory.EnumerateFileSystemEntries(cache).Any())
                        {
                            PathKind current = PathKindUtil.KindOf(configPath);
                            ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
                            ConfigSwapPrimitives.ClearPath(configPath, current);
                            ConfigSwapPrimitives.MoveAs(cache, configPath,
                                mark is null
                                    ? (string.IsNullOrWhiteSpace(Path.GetExtension(configPath)) ? PathKind.Dir : PathKind.File)
                                    : ConfigSwapPrimitives.RestoreKind(mark));
                            ConfigSessionMark.Clear(scriptId, userName);
                        }
                    }
                });
            }
            catch (Exception rollback)
            {
                Logger.Error($"[错误] 配置准备失败且回滚异常：{rollback.Message}");
            }
            return false;
        }
    }

    /// <summary>运行结束后还原：清 config（运行产物），original → config 还原原配置。失败保留标记与缓存，交由自愈。</summary>
    public static string? RestoreAfterRun(string scriptId, string userName, string configPath)
    {
        string? error = null;
        try
        {
            ConfigSwapPrimitives.WithSwapLock(scriptId, () =>
            {
                ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
                if (mark is null)
                {
                    mark = new ConfigSessionMark
                    {
                        ScriptId = scriptId,
                        UserName = userName,
                        ConfigPath = configPath,
                        OriginalKind = PathKindUtil.Text(PathKindUtil.KindOf(configPath)),
                    };
                }
                ConfigSwapSession.DoRestore(scriptId, userName, mark);
            });
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Audit.Log(Audit.System, "配置还原失败（保留现场）", $"脚本 {scriptId} / 用户 {userName}：{error}，缓存区位于 {CacheDir(scriptId, userName)}");
        }
        return error;
    }

    /// <summary>编辑配置开始：config → original（移动），store → config（复制）。</summary>
    public static string? PrepareForEdit(string scriptId, string userName, string configPath)
    {
        return PrepareForRun(scriptId, userName, configPath, out string? error) ? null : (error ?? "配置交换失败");
    }

    /// <summary>全新配置编辑开始（无快照首选）：标记先行（EditMode=fresh）→ config 存在则移入 original 缓存区，
    /// 让脚本主程序在空配置位置生成全新配置。失败自动回滚还原现场。</summary>
    public static string? PrepareForEditFresh(string scriptId, string userName, string configPath)
    {
        string? error = null;
        try
        {
            ConfigSwapPrimitives.WithSwapLock(scriptId, () =>
            {
                ConfigSwapSession.RecoverIfNeeded(scriptId, userName, configPath);
                var mark = new ConfigSessionMark
                {
                    ScriptId = scriptId,
                    UserName = userName,
                    ConfigPath = configPath,
                    OriginalKind = PathKindUtil.Text(PathKindUtil.KindOf(configPath)),
                    Phase = "edit",
                    EditMode = "fresh",
                };
                mark.Write();
                string cache = CacheDir(scriptId, userName);
                ConfigSwapPrimitives.ClearPath(cache, PathKindUtil.KindOf(cache));
                if (PathKindUtil.KindOf(configPath) != PathKind.Missing)
                {
                    ConfigSwapPrimitives.MoveAs(configPath, cache, PathKind.Dir);
                }
            });
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try
            {
                ConfigSwapPrimitives.WithSwapLock(scriptId, () =>
                {
                    ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
                    if (mark is not null)
                    {
                        ConfigSwapSession.DoRestore(scriptId, userName, mark);
                    }
                });
            }
            catch (Exception rollback)
            {
                Logger.Error($"[错误] 全新配置编辑准备失败且回滚异常：{rollback.Message}");
            }
        }
        return error;
    }

    /// <summary>复用配置编辑开始（无快照）：仅写会话标记（EditMode=reuse）记录会话，无任何文件动作，
    /// 脚本主程序直接编辑现场配置文件。</summary>
    public static string? PrepareForEditReuse(string scriptId, string userName, string configPath)
    {
        string? error = null;
        try
        {
            ConfigSwapPrimitives.WithSwapLock(scriptId, () =>
            {
                ConfigSwapSession.RecoverIfNeeded(scriptId, userName, configPath);
                var mark = new ConfigSessionMark
                {
                    ScriptId = scriptId,
                    UserName = userName,
                    ConfigPath = configPath,
                    OriginalKind = PathKindUtil.Text(PathKindUtil.KindOf(configPath)),
                    Phase = "edit",
                    EditMode = "reuse",
                };
                mark.Write();
            });
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        return error;
    }

    /// <summary>编辑会话隐藏目录：暂存 config 同目录的其他配置文件（如 BetterGI 自带配置），使编辑目标成为唯一可选配置。</summary>
    public static string HiddenConfigDir(string scriptId, string userName)
    {
        return ConfigSwapPaths.HiddenConfigDir(scriptId, userName);
    }

    /// <summary>恢复隐藏配置（幂等：隐藏目录为空则无操作）；编辑会话开始前调用可自愈崩溃残留。</summary>
    public static void RestoreHiddenConfigs(string scriptId, string userName, string configPath)
    {
        string hideDir = HiddenConfigDir(scriptId, userName);
        if (!Directory.Exists(hideDir) || !Directory.EnumerateFileSystemEntries(hideDir).Any())
        {
            return;
        }
        string? dir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
        foreach (string file in Directory.GetFiles(hideDir))
        {
            try
            {
                File.Move(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 恢复隐藏配置失败（保持原样）：{file}（{ex.Message}）");
            }
        }
        try
        {
            if (Directory.Exists(hideDir) && !Directory.EnumerateFileSystemEntries(hideDir).Any())
            {
                Directory.Delete(hideDir);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>编辑会话隐藏 config 同目录下其他配置文件（仅专项脚本 + config 为单文件；排除 ConfigPath 文件本身，忽略大小写）。</summary>
    public static bool HideOtherConfigs(ScriptInstance script, string scriptId, string userName)
    {
        if (string.IsNullOrWhiteSpace(script.PluginType) || !File.Exists(script.ConfigPath))
        {
            return false;
        }
        string? dir = Path.GetDirectoryName(script.ConfigPath);
        if (string.IsNullOrWhiteSpace(dir))
        {
            return false;
        }
        string targetName = Path.GetFileName(script.ConfigPath);
        string[] others = Directory.GetFiles(dir, "*.json")
            .Where(file => !Path.GetFileName(file).Equals(targetName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (others.Length == 0)
        {
            return false;
        }
        string hideDir = HiddenConfigDir(scriptId, userName);
        Directory.CreateDirectory(hideDir);
        foreach (string file in others)
        {
            try
            {
                File.Move(file, Path.Combine(hideDir, Path.GetFileName(file)));
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 隐藏配置失败（保持原样）：{file}（{ex.Message}）");
            }
        }
        return true;
    }

    /// <summary>编辑配置提交：config → store 复制入库；normal/fresh 模式随后 original → config 还原原配置，reuse 模式无回移动作。</summary>
    public static string? CommitEdit(string scriptId, string userName, string configPath)
    {
        string? error = null;
        try
        {
            ConfigSwapPrimitives.WithSwapLock(scriptId, () =>
            {
                ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
                if (mark is null)
                {
                    throw new IOException("未找到配置编辑会话");
                }
                string store = StoreDir(scriptId, userName);
                ConfigSwapSession.CommitStoreSnapshot(configPath, store);
                if (!IsReuseEdit(mark))
                {
                    ConfigSwapSession.DoRestore(scriptId, userName, mark);
                }
                ConfigSessionMark.Clear(scriptId, userName);
            });
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        return error;
    }

    /// <summary>编辑配置取消：normal/fresh 模式清 config（编辑/生成产物）后 original → config 还原原配置；reuse 模式无文件动作。</summary>
    public static string? CancelEdit(string scriptId, string userName, string configPath)
    {
        string? error = null;
        try
        {
            ConfigSwapPrimitives.WithSwapLock(scriptId, () =>
            {
                ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
                if (mark is null)
                {
                    throw new IOException("未找到配置编辑会话");
                }
                if (!IsReuseEdit(mark))
                {
                    ConfigSwapSession.DoRestore(scriptId, userName, mark);
                }
                ConfigSessionMark.Clear(scriptId, userName);
            });
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        return error;
    }

    /// <summary>reuse 编辑会话只把现场配置复制入库，无 original 现场可还原；fresh/normal 共用现有还原路径。</summary>
    private static bool IsReuseEdit(ConfigSessionMark? mark)
    {
        return string.Equals(mark?.EditMode, "reuse", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>删除脚本时清理其全部数据目录。</summary>
    public static bool RemoveScriptData(string scriptId)
    {
        return ConfigSwapPaths.RemoveScriptData(scriptId);
    }

    /// <summary>删除用户绑定时清理其 UserId 数据目录。</summary>
    public static void RemoveUserData(string scriptId, string userKey)
    {
        ConfigSwapPaths.RemoveUserData(scriptId, userKey);
    }
}
