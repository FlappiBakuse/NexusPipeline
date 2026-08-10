using System.Collections.Concurrent;
using NexusPipeline.Models;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>
/// 配置储存管理对外门面（v0.5.0 拆分）：保持全部外部 API 签名不变。
/// 实现分层：文件原语 <see cref="ConfigSwapPrimitives"/>（安全移动/原子替换/重试/跨进程互斥）、
/// 会话与恢复 <see cref="ConfigSwapSession"/>（.session 标记/门禁/回滚/finally 还原/启动扫描恢复）、
/// 数据目录 <see cref="ConfigSwapPaths"/>。数据保全序：cache（原配置）&gt; config &gt; store（可重建）。
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

    public static ScriptUser? FindEnabledUser(ScriptInstance script, string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }
        return script.Users.FirstOrDefault(user => user.Enabled && string.Equals(user.Name, userName, StringComparison.OrdinalIgnoreCase));
    }

    /* ---------------- 对外操作 ---------------- */

    /// <summary>首次添加用户：把当前配置内容复制为程序内部储存配置（config 保留）。源不存在时建立空快照。</summary>
    public static string? SnapshotOnAddUser(ScriptInstance script, string userName)
    {
        string store = StoreDir(script.Id, userName);
        string? error = null;
        ConfigSwapPrimitives.WithSwapLock(script.Id, () =>
        {
            try
            {
                if (Directory.Exists(store) && Directory.EnumerateFileSystemEntries(store).Any())
                {
                    return;
                }
                ConfigSwapPrimitives.ClearPath(store, PathKindUtil.KindOf(store));
                if (string.IsNullOrWhiteSpace(script.ConfigPath))
                {
                    Directory.CreateDirectory(store);
                    return;
                }
                ConfigSwapPrimitives.CopyAs(script.ConfigPath, store, PathKind.Dir);
                Audit.Log(Audit.Web, "建立用户初始配置快照", $"{script.Name} / {userName} → {store}");
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        });
        return error;
    }

    /// <summary>运行前准备：config → cache（移动），store → config（复制）。失败自动回滚并还原现场。</summary>
    public static bool PrepareForRun(string scriptId, string userName, string configPath, out string? error)
    {
        error = null;
        bool prepared = false;
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
                    Phase = "run",
                };
                string cache = CacheDir(scriptId, userName);
                string store = StoreDir(scriptId, userName);
                // 标记先行：任何时刻崩溃（含移动配置前后）都可恢复——cache 空时恢复仅清标记（现场未动），cache 有内容时完整还原。
                mark.Write();
                ConfigSwapPrimitives.ClearPath(cache, PathKindUtil.KindOf(cache));
                ConfigSwapPrimitives.MoveAs(configPath, cache, PathKind.Dir);
                if (Directory.Exists(store) && Directory.EnumerateFileSystemEntries(store).Any())
                {
                    ConfigSwapPrimitives.CopyAs(store, configPath, ConfigSwapPrimitives.RestoreKind(mark));
                }
                else if (PathKindUtil.Parse(mark.OriginalKind) != PathKind.File)
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
                            PathKind original = PathKindUtil.KindOf(configPath);
                            ConfigSwapPrimitives.ClearPath(configPath, original);
                            ConfigSwapPrimitives.MoveAs(cache, configPath, PathKind.Dir);
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

    /// <summary>运行结束后还原：清 config（运行产物），cache → config 还原原配置。失败保留标记与缓存，交由自愈。</summary>
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

    /// <summary>编辑配置开始：config → cache（移动），store → config（复制）。</summary>
    public static string? PrepareForEdit(string scriptId, string userName, string configPath)
    {
        return PrepareForRun(scriptId, userName, configPath, out string? error) ? null : (error ?? "配置交换失败");
    }

    /// <summary>编辑配置提交：先 config → store（新配置入库），再 cache → config（还原原配置）。</summary>
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
                string cache = CacheDir(scriptId, userName);
                ConfigSwapPrimitives.ClearPath(store, PathKindUtil.KindOf(store));
                ConfigSwapPrimitives.MoveAs(configPath, store, PathKind.Dir);
                if (Directory.Exists(cache) && Directory.EnumerateFileSystemEntries(cache).Any())
                {
                    ConfigSwapPrimitives.MoveAs(cache, configPath, ConfigSwapPrimitives.RestoreKind(mark));
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

    /// <summary>编辑配置取消：清 config（编辑产物），cache → config 还原原配置。</summary>
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
                ConfigSwapSession.DoRestore(scriptId, userName, mark);
            });
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        return error;
    }

    /* ---------------- 配置替换 / 恢复（转发 ConfigSwapSession） ---------------- */

    /// <summary>应用配置替换：把 script 目录内文件复制覆盖到 config 对应位置；首次替换前备份原始内容到 replace-backup（含 .meta 记录 configPath 与新增文件清单）。</summary>
    public static string? ApplyConfigReplacements(string scriptId, string? userName, string configPath, List<string> replacements)
    {
        return ConfigSwapSession.ApplyConfigReplacements(scriptId, userName, configPath, replacements);
    }

    /// <summary>还原配置替换：从 replace-backup 恢复全部被替换文件（按 .meta 记录的 configPath），删除替换期间新增的文件，随后清理备份目录。</summary>
    public static void RestoreConfigReplacements(string scriptId, string? userName)
    {
        ConfigSwapSession.RestoreConfigReplacements(scriptId, userName);
    }

    /// <summary>操作前自愈：若存在未完成的交换标记且缓存区有内容，先完成还原（安全优先：原配置必还原）。失败交由后台重试。</summary>
    public static void RecoverIfNeeded(string scriptId, string userName, string configPath)
    {
        ConfigSwapSession.RecoverIfNeeded(scriptId, userName, configPath);
    }

    /// <summary>启动恢复：扫描全部残留标记并还原（幂等；cache 为空则仅清标记，不动现场）；同时恢复未还原的配置替换。</summary>
    public static void RecoverInterrupted()
    {
        ConfigSwapSession.RecoverInterrupted();
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
    public static void RemoveScriptData(string scriptId)
    {
        ConfigSwapPaths.RemoveScriptData(scriptId);
    }

    /// <summary>删除用户时清理其数据目录。</summary>
    public static void RemoveUserData(string scriptId, string userName)
    {
        ConfigSwapPaths.RemoveUserData(scriptId, userName);
    }

    /// <summary>用户改名时迁移其数据目录；失败返回错误信息（由调用方决定不落盘，名称与数据保持原样）。</summary>
    public static string? RenameUserData(string scriptId, string oldName, string newName)
    {
        return ConfigSwapPaths.RenameUserData(scriptId, oldName, newName);
    }
}
