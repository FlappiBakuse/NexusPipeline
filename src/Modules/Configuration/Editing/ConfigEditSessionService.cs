using System.Collections.Concurrent;
using System.Text;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Shared.Logging;
namespace NexusPipeline.Modules.Configuration.Editing;


/// <summary>
/// 配置储存管理对外门面（拆分）：保持全部外部 API 签名不变。
/// 实现分层：文件原语 <see cref="ConfigSwapPrimitives"/>（安全移动/原子替换/重试/跨进程互斥）、
/// 会话与恢复 <see cref="ConfigSwapSession"/>（.session 标记/门禁/回滚/finally 还原/启动扫描恢复）、
/// 数据目录 <see cref="ConfigPaths"/>。数据保全序：original（原配置）&gt; config &gt; store（可重建）。
/// </summary>
internal static class ConfigEditSessionService
{

    /// <summary>编辑配置开始：config → original（移动），store → config（复制）。</summary>
    public static string? PrepareForEdit(
        string scriptId,
        string userName,
        string configPath,
        ConfigSessionRuntimeMetadata? metadata = null,
        IReadOnlyList<string>? extraConfigPaths = null,
        ConfigEditPreparationOptions? options = null)
    {
        if (!ConfigExchangeService.PrepareForRun(scriptId, userName, configPath, out string? error, metadata, extraConfigPaths))
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
                    string cache = ConfigPaths.CacheDir(scriptId, userName);
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
        string marker = ConfigPaths.StoreTransactionBlockedPath(scriptId, userName);
        if (File.Exists(marker))
        {
            throw new IOException($"配置快照事务已被阻断，需人工核查后解除：{marker}");
        }
    }
}
