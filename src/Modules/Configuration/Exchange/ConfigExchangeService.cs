using System.Collections.Concurrent;
using System.Text;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Shared.Logging;
namespace NexusPipeline.Modules.Configuration.Exchange;


/// <summary>
/// 配置储存管理对外门面（拆分）：保持全部外部 API 签名不变。
/// 实现分层：文件原语 <see cref="ConfigSwapPrimitives"/>（安全移动/原子替换/重试/跨进程互斥）、
/// 会话与恢复 <see cref="ConfigSwapSession"/>（.session 标记/门禁/回滚/finally 还原/启动扫描恢复）、
/// 数据目录 <see cref="ConfigPaths"/>。数据保全序：original（原配置）&gt; config &gt; store（可重建）。
/// </summary>
internal static class ConfigExchangeService
{

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
                if (File.Exists(ConfigPaths.StoreTransactionBlockedPath(scriptId, userName)))
                {
                    throw new IOException($"配置快照事务已被阻断，需人工核查后解除：{ConfigPaths.StoreTransactionBlockedPath(scriptId, userName)}");
                }
                string store = ConfigPaths.StoreDir(scriptId, userName);
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
                    throw new IOException($"附加配置存在无法由当前会话解释的恢复现场，已保留并阻断运行：{ConfigPaths.OriginalExtraRoot(scriptId, userName)}");
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
                string cache = ConfigPaths.CacheDir(scriptId, userName);
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
                        string cache = ConfigPaths.CacheDir(scriptId, userName);
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
            Audit.Log(Audit.System, "配置还原失败（保留现场）", $"脚本 {scriptId} / 用户 {userName}：{error}，缓存区位于 {ConfigPaths.CacheDir(scriptId, userName)}");
        }
        return error;
    }
}
