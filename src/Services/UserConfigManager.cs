using System.Collections.Concurrent;
using System.Text;
using NexusPipeline.Extensibility;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Utilities;
using NexusPipeline.Services.Configuration;

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

    public static string EditIsolationDir(string scriptId, string userName)
    {
        return ConfigSwapPaths.EditIsolationDir(scriptId, userName);
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
    /// v0.12.8 起绑定不再建立快照：快照为空且现场配置存在时，先把现场配置复制为初始快照（复用语义），再执行交换。
    /// extraConfigPaths 非空时附加配置路径在主配置之前完成对称的 adopt/备份/快照覆盖；任一条路径失败都阻断主流程并回滚已准备现场。</summary>
    public static bool PrepareForRun(
        string scriptId,
        string userName,
        string configPath,
        out string? error,
        ConfigSessionRuntimeMetadata? metadata = null,
        IReadOnlyList<string>? extraConfigPaths = null)
    {
        error = null;
        bool prepared = false;
        try
        {
            ConfigSwapPrimitives.WithSwapLock(scriptId, () =>
            {
                ConfigSwapSession.RecoverIfNeeded(scriptId, userName, configPath);
                if (File.Exists(ConfigSwapPaths.StoreTransactionBlockedPath(scriptId, userName)))
                {
                    throw new IOException($"配置快照事务已被阻断，需人工核查后解除：{ConfigSwapPaths.StoreTransactionBlockedPath(scriptId, userName)}");
                }
                string store = StoreDir(scriptId, userName);
                PathKind currentConfigKind = PathKindUtil.KindOf(configPath);
                ConfigStoreMetadata expectedMetadata = ConfigStoreMetadata.For(configPath, metadata);
                bool hasStore = Directory.Exists(store) && Directory.EnumerateFileSystemEntries(store).Any();
                ConfigStoreMetadata? existingMetadata = hasStore
                    ? ConfigStoreMetadata.Load(scriptId, userName)
                    : null;
                if (hasStore && existingMetadata is null)
                {
                    throw new IOException($"配置快照缺少或无法读取当前格式元数据，已保留原快照：{store}");
                }
                if (hasStore
                    && existingMetadata is not null
                    && !existingMetadata.Matches(expectedMetadata))
                {
                    if (currentConfigKind == PathKind.Missing)
                    {
                        throw new IOException($"配置路径已变更但新位置不存在：{configPath}；旧配置快照已保留，未自动复用");
                    }
                    // 配置定位或形态发生变化时按当前现场重新建立快照；当前 pre-release 数据协议不跨契约搬运旧快照。
                    ConfigSwapPrimitives.ClearPath(store, PathKindUtil.KindOf(store));
                    ConfigSwapPrimitives.CopyAs(configPath, store, PathKind.Dir);
                    hasStore = Directory.Exists(store) && Directory.EnumerateFileSystemEntries(store).Any();
                    Audit.Log(Audit.System, "按当前配置重新建立快照", $"脚本 {scriptId} / 用户 {userName}：{configPath} → {store}");
                }
                if (!hasStore && currentConfigKind != PathKind.Missing)
                {
                    ConfigSwapPrimitives.ClearPath(store, PathKindUtil.KindOf(store));
                    ConfigSwapPrimitives.CopyAs(configPath, store, PathKind.Dir);
                    hasStore = Directory.Exists(store) && Directory.EnumerateFileSystemEntries(store).Any();
                    Audit.Log(Audit.System, "运行前建立配置快照", $"脚本 {scriptId} / 用户 {userName}：{configPath} → {store}");
                }
                if (hasStore)
                {
                    ConfigStoreMetadata.Save(scriptId, userName, expectedMetadata);
                }
                List<string> declaredExtraPaths = metadata?.ExtraConfigPaths?
                    .Select(item => item.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToList() ?? new List<string>();
                if (declaredExtraPaths.Count == 0 && extraConfigPaths is { Count: > 0 })
                {
                    declaredExtraPaths = extraConfigPaths.ToList();
                }
                if (ExtraConfigSync.HasResidue(scriptId, userName))
                {
                    throw new IOException($"附加配置存在无法由当前会话解释的恢复现场，已保留并阻断运行：{ConfigSwapPaths.OriginalExtraRoot(scriptId, userName)}");
                }
                // 在当前恢复流程完成后捕获形态，确保 .session 冻结的是本次会话真正要移动的现场。
                List<ConfigSessionExtraPath> frozenExtraPaths = ConfigSessionMark.FromExtraPaths(declaredExtraPaths);
                var mark = new ConfigSessionMark
                {
                    ScriptId = scriptId,
                    UserId = userName,
                    ConfigPath = configPath,
                    SessionPhase = "run",
                    ConfigKind = PathKindUtil.Text(PathKindUtil.KindOf(configPath)),
                    WorkingDirectory = metadata?.WorkingDirectory ?? "",
                    LaunchExe = metadata?.LaunchExe ?? "",
                    ProcessIdentity = metadata?.ProcessIdentity ?? "",
                    ProfileHash = metadata?.ProfileHash ?? "",
                    PluginName = metadata?.PluginName ?? "",
                    PluginVersion = metadata?.PluginVersion ?? "",
                    ExtraConfigPaths = frozenExtraPaths,
                };
                // 标记先行：任何时刻崩溃（含 extra/main 配置移动前后）都可恢复。
                mark.Write();
                if (mark.ExtraConfigPaths.Count > 0)
                {
                    ExtraConfigSync.PrepareAll(scriptId, userName, mark.ExtraConfigPaths);
                }
                string cache = CacheDir(scriptId, userName);
                ConfigSwapPrimitives.ClearPath(cache, PathKindUtil.KindOf(cache));
                ConfigSwapPrimitives.MoveAs(configPath, cache, PathKind.Dir);
                if (Directory.Exists(store) && Directory.EnumerateFileSystemEntries(store).Any())
                {
                    ConfigSwapPrimitives.CopyAs(store, configPath, ConfigSwapPrimitives.RestoreKind(mark));
                }
                else if (PathKindUtil.Parse(mark.ConfigKind) == PathKind.Dir)
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
            if ((File.Exists(ConfigSessionMark.MarkFile(scriptId, userName))
                    || File.Exists(ConfigSessionMark.BackupMarkFile(scriptId, userName)))
                && ConfigSessionMark.TryRead(scriptId, userName) is null)
            {
                // 主/备标记均损坏时无法确认原配置路径，保留 cache/config 现场，禁止回退到扩展名猜测。
                Logger.Error($"[错误] 配置准备失败且会话标记不可解析，保留现场等待人工处理：脚本 {scriptId} / 用户 {userName}");
                return false;
            }
            try
            {
                ConfigSwapPrimitives.WithSwapLock(scriptId, () =>
                {
                    if (prepared)
                    {
                        ConfigSwapSession.DoRestore(scriptId, userName, ConfigSessionMark.TryRead(scriptId, userName) ?? new ConfigSessionMark
                        {
                            ScriptId = scriptId,
                            UserId = userName,
                            ConfigPath = configPath,
                            SessionPhase = "run",
                            ConfigKind = PathKindUtil.Text(PathKindUtil.KindOf(configPath)),
                        });
                    }
                    else
                    {
                        string cache = CacheDir(scriptId, userName);
                        ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
                        if (mark is not null)
                        {
                            // extra/main 任一准备阶段失败时，统一按会话标记回滚，确保已准备的附加现场
                            // 与主配置一起恢复；不能只处理 original cache 后清除标记。
                            ConfigSwapSession.DoRestore(scriptId, userName, mark);
                        }
                        else if (Directory.Exists(cache) && Directory.EnumerateFileSystemEntries(cache).Any())
                        {
                            PathKind current = PathKindUtil.KindOf(configPath);
                            ConfigSwapPrimitives.ClearPath(configPath, current);
                            ConfigSwapPrimitives.MoveAs(cache, configPath,
                                string.IsNullOrWhiteSpace(Path.GetExtension(configPath)) ? PathKind.Dir : PathKind.File);
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
    public static string? RestoreAfterRun(string scriptId, string userName, string configPath, IReadOnlyList<string>? extraConfigPaths = null)
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
                        UserId = userName,
                        ConfigPath = configPath,
                        SessionPhase = "run",
                        ConfigKind = PathKindUtil.Text(PathKindUtil.KindOf(configPath)),
                    };
                }
                ConfigSwapSession.DoRestore(scriptId, userName, mark);
                if (extraConfigPaths is { Count: > 0 })
                {
                    ExtraConfigSync.RestoreAll(
                        scriptId,
                        userName,
                        ConfigSessionMark.FromExtraPaths(extraConfigPaths));
                }
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
    public static string? PrepareForEdit(
        string scriptId,
        string userName,
        string configPath,
        ConfigSessionRuntimeMetadata? metadata = null,
        IReadOnlyList<string>? extraConfigPaths = null,
        ConfigEditPreparationOptions? options = null)
    {
        if (!PrepareForRun(scriptId, userName, configPath, out string? error, metadata, extraConfigPaths))
        {
            return error ?? "配置交换失败";
        }
        if (options?.IsolateSiblingCandidates != true)
        {
            return null;
        }

        try
        {
            ConfigSwapPrimitives.WithSwapLock(scriptId, () =>
            {
                ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
                if (mark is null)
                {
                    throw new IOException("配置交换已准备但缺少会话标记");
                }
                mark.SessionPhase = "edit";
                mark.EditMode = "normal";
                mark.EditIsolationPaths = EditConfigIsolation.BuildEntries(
                    options.CandidatePaths,
                    configPath,
                    includeConfigPath: false);
                mark.PendingConfigInput = null;
                mark.Write();
                if (mark.EditIsolationPaths.Count > 0)
                {
                    // normal 已由 PrepareForRun 把 store 工作副本放回 config；此处只迁出兄弟候选。
                    EditConfigIsolation.Prepare(scriptId, userName, mark, copySelectedWorking: false);
                }
            });
            return null;
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
                Logger.Error($"[错误] 普通配置编辑隔离失败且回滚异常：{rollback.Message}");
            }
            return error;
        }
    }

    /// <summary>全新配置编辑开始：标记先行，按编辑声明隔离候选并准备附加配置工作副本。</summary>
    public static string? PrepareForEditFresh(
        string scriptId,
        string userName,
        string configPath,
        ConfigSessionRuntimeMetadata? metadata = null,
        IReadOnlyList<string>? extraConfigPaths = null,
        ConfigEditPreparationOptions? options = null)
    {
        string? error = null;
        try
        {
            ConfigSwapPrimitives.WithSwapLock(scriptId, () =>
            {
                ConfigSwapSession.RecoverIfNeeded(scriptId, userName, configPath);
                ThrowIfStoreTransactionBlocked(scriptId, userName);
                var mark = new ConfigSessionMark
                {
                    ScriptId = scriptId,
                    UserId = userName,
                    ConfigPath = configPath,
                    SessionPhase = "edit",
                    ConfigKind = PathKindUtil.Text(PathKindUtil.KindOf(configPath)),
                    EditMode = "fresh",
                    WorkingDirectory = metadata?.WorkingDirectory ?? "",
                    LaunchExe = metadata?.LaunchExe ?? "",
                    ProcessIdentity = metadata?.ProcessIdentity ?? "",
                    ProfileHash = metadata?.ProfileHash ?? "",
                    PluginName = metadata?.PluginName ?? "",
                    PluginVersion = metadata?.PluginVersion ?? "",
                    ExtraConfigPaths = FreezeExtraPaths(metadata, extraConfigPaths),
                    EditIsolationPaths = options?.IsolateSiblingCandidates == true
                        ? EditConfigIsolation.BuildEntries(options.CandidatePaths, configPath)
                        : new List<ConfigSessionIsolationPath>(),
                    PendingConfigInput = options?.PendingConfigInput?.Clone(),
                };
                mark.Write();
                if (mark.EditIsolationPaths.Count > 0)
                {
                    EditConfigIsolation.Prepare(scriptId, userName, mark, copySelectedWorking: false);
                }
                else
                {
                    string cache = CacheDir(scriptId, userName);
                    ConfigSwapPrimitives.ClearPath(cache, PathKindUtil.KindOf(cache));
                    if (PathKindUtil.KindOf(configPath) != PathKind.Missing)
                    {
                        ConfigSwapPrimitives.MoveAs(configPath, cache, PathKind.Dir);
                    }
                }
                if (mark.ExtraConfigPaths.Count > 0)
                {
                    ExtraConfigSync.PrepareFirstEditAll(scriptId, userName, mark.ExtraConfigPaths);
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

    /// <summary>复用配置编辑开始：可将兄弟候选隔离，并复制选中候选作为工作副本。</summary>
    public static string? PrepareForEditReuse(
        string scriptId,
        string userName,
        string configPath,
        ConfigSessionRuntimeMetadata? metadata = null,
        IReadOnlyList<string>? extraConfigPaths = null,
        ConfigEditPreparationOptions? options = null)
    {
        string? error = null;
        try
        {
            ConfigSwapPrimitives.WithSwapLock(scriptId, () =>
            {
                ConfigSwapSession.RecoverIfNeeded(scriptId, userName, configPath);
                ThrowIfStoreTransactionBlocked(scriptId, userName);
                var mark = new ConfigSessionMark
                {
                    ScriptId = scriptId,
                    UserId = userName,
                    ConfigPath = configPath,
                    SessionPhase = "edit",
                    ConfigKind = PathKindUtil.Text(PathKindUtil.KindOf(configPath)),
                    EditMode = "reuse",
                    WorkingDirectory = metadata?.WorkingDirectory ?? "",
                    LaunchExe = metadata?.LaunchExe ?? "",
                    ProcessIdentity = metadata?.ProcessIdentity ?? "",
                    ProfileHash = metadata?.ProfileHash ?? "",
                    PluginName = metadata?.PluginName ?? "",
                    PluginVersion = metadata?.PluginVersion ?? "",
                    ExtraConfigPaths = FreezeExtraPaths(metadata, extraConfigPaths),
                    EditIsolationPaths = options?.IsolateSiblingCandidates == true
                        ? EditConfigIsolation.BuildEntries(options.CandidatePaths, configPath)
                        : new List<ConfigSessionIsolationPath>(),
                    PendingConfigInput = options?.PendingConfigInput?.Clone(),
                };
                mark.Write();
                if (mark.EditIsolationPaths.Count > 0)
                {
                    EditConfigIsolation.Prepare(scriptId, userName, mark, copySelectedWorking: true);
                }
                if (mark.ExtraConfigPaths.Count > 0)
                {
                    ExtraConfigSync.PrepareFirstEditAll(scriptId, userName, mark.ExtraConfigPaths);
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
                Logger.Error($"[错误] 复用配置编辑准备失败且回滚异常：{rollback.Message}");
            }
        }
        return error;
    }

    /// <summary>编辑配置提交：主配置与附加配置按各自事务入库，再还原所有编辑现场。</summary>
    public static string? CommitEdit(string scriptId, string userName, string configPath, IReadOnlyList<string>? extraConfigPaths = null)
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
                if (mark.PendingConfigInput is not null)
                {
                    mark.SessionPhase = "edit-commit-pending";
                    mark.Write();
                }
                ConfigStoreTransaction.Apply(
                    scriptId,
                    userName,
                    configPath,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    null,
                    null,
                    mark);
                IReadOnlyList<string> frozenExtraPaths = mark.ExtraConfigPaths
                    .Select(item => item.Path)
                    .ToArray();
                if (frozenExtraPaths.Count == 0 && extraConfigPaths is { Count: > 0 })
                {
                    frozenExtraPaths = extraConfigPaths;
                }
                if (frozenExtraPaths.Count > 0)
                {
                    ExtraConfigSync.SyncAllFromSite(scriptId, userName, frozenExtraPaths, "编辑提交");
                }
                if (!IsReuseEdit(mark)
                    || mark.EditIsolationPaths.Count > 0
                    || mark.ExtraConfigPaths.Count > 0)
                {
                    ConfigSwapSession.DoRestore(scriptId, userName, mark);
                }
                if (mark.PendingConfigInput is null)
                {
                    ConfigSessionMark.Clear(scriptId, userName);
                }
            });
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        return error;
    }

    /// <summary>编辑配置取消：清理工作副本并还原主配置、兄弟候选和附加配置现场。</summary>
    public static string? CancelEdit(string scriptId, string userName, string configPath, IReadOnlyList<string>? extraConfigPaths = null)
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
                if (!IsReuseEdit(mark)
                    || mark.EditIsolationPaths.Count > 0
                    || mark.ExtraConfigPaths.Count > 0)
                {
                    ConfigSwapSession.DoRestore(scriptId, userName, mark);
                    if (mark.ExtraConfigPaths.Count == 0 && extraConfigPaths is { Count: > 0 })
                    {
                        ExtraConfigSync.RestoreAll(
                            scriptId,
                            userName,
                            ConfigSessionMark.FromExtraPaths(extraConfigPaths));
                    }
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

    private static List<ConfigSessionExtraPath> FreezeExtraPaths(
        ConfigSessionRuntimeMetadata? metadata,
        IReadOnlyList<string>? extraConfigPaths)
    {
        if (metadata?.ExtraConfigPaths is { Count: > 0 } frozen)
        {
            return frozen.Select(item => item.Clone()).ToList();
        }
        return ConfigSessionMark.FromExtraPaths(extraConfigPaths);
    }

    private static void ThrowIfStoreTransactionBlocked(string scriptId, string userName)
    {
        string marker = ConfigSwapPaths.StoreTransactionBlockedPath(scriptId, userName);
        if (File.Exists(marker))
        {
            throw new IOException($"配置快照事务已被阻断，需人工核查后解除：{marker}");
        }
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
