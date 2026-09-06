using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Configuration;

/// <summary>
/// 附加配置路径（专项插件 resolve.json extraConfigPaths）的隔离、同步与还原层。
/// 路径由插件声明，生命周期与恢复由宿主负责；运行会话使用 ConfigSessionMark 冻结绝对路径和现场形态。
/// 全部方法要求调用方已持有该脚本的配置交换锁（ConfigSwapPrimitives.WithSwapLock）。
/// </summary>
internal static class ExtraConfigSync
{
    /// <summary>兼容测试与旧调用：从当前现场建立恢复描述后执行批量准备。</summary>
    public static void PrepareAll(string scriptId, string userKey, IReadOnlyList<string> sitePaths)
    {
        PrepareAll(scriptId, userKey, ConfigSessionMark.FromExtraPaths(sitePaths));
    }

    /// <summary>
    /// 使用已写入会话标记的恢复描述批量准备。任一条路径失败都会反向还原已尝试路径并抛出，
    /// 调用方据此阻断外部脚本启动。
    /// </summary>
    public static void PrepareAll(
        string scriptId,
        string userKey,
        IReadOnlyList<ConfigSessionExtraPath> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }
        ValidateEntries(entries);
        var attempted = new List<ConfigSessionExtraPath>(entries.Count);
        try
        {
            foreach (ConfigSessionExtraPath entry in entries)
            {
                attempted.Add(entry);
                Prepare(scriptId, userKey, entry);
            }
        }
        catch
        {
            foreach (ConfigSessionExtraPath entry in attempted.AsEnumerable().Reverse())
            {
                try
                {
                    Restore(scriptId, userKey, entry);
                }
                catch (Exception rollback)
                {
                    Logger.Error($"[附加配置] 批量准备回滚失败，保留恢复现场（脚本 {scriptId} / 用户 {userKey} / {entry.Path}）：{rollback.Message}");
                }
            }
            throw;
        }
    }

    /// <summary>
    /// 首次编辑的附加配置准备：原现场进入 original-extra，再复制回现场作为工作副本。
    /// 该阶段保持 store-extra 未创建，保存操作才会建立用户快照。
    /// </summary>
    public static void PrepareFirstEditAll(
        string scriptId,
        string userKey,
        IReadOnlyList<ConfigSessionExtraPath> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }
        ValidateEntries(entries);
        var attempted = new List<ConfigSessionExtraPath>(entries.Count);
        try
        {
            foreach (ConfigSessionExtraPath entry in entries)
            {
                attempted.Add(entry);
                PrepareFirstEdit(scriptId, userKey, entry);
            }
        }
        catch
        {
            foreach (ConfigSessionExtraPath entry in attempted.AsEnumerable().Reverse())
            {
                try
                {
                    Restore(scriptId, userKey, entry);
                }
                catch (Exception rollback)
                {
                    Logger.Error($"[附加配置] 首次编辑准备回滚失败，保留恢复现场（脚本 {scriptId} / 用户 {userKey} / {entry.Path}）：{rollback.Message}");
                }
            }
            throw;
        }
    }

    /// <summary>现场内容差异同步入快照；每条附加路径的快照替换由独立事务提交。</summary>
    public static void SyncAllFromSite(
        string scriptId,
        string userKey,
        IReadOnlyList<string> sitePaths,
        string phase)
    {
        foreach (string sitePath in sitePaths)
        {
            try
            {
                SyncFromSite(scriptId, userKey, sitePath, phase);
            }
            catch (Exception ex)
            {
                // 同步失败保留旧快照，收尾继续进入现场还原；事务本身已经完成回滚或保留恢复材料。
                Logger.Warn($"[附加配置] 同步失败（脚本 {scriptId} / 用户 {userKey}）：{sitePath}：{ex.Message}");
            }
        }
    }

    /// <summary>兼容无会话标记的旧现场还原；失败保留 original-extra 并记录警告。</summary>
    public static void RestoreAll(string scriptId, string userKey, IReadOnlyList<string> sitePaths)
    {
        foreach (string sitePath in sitePaths)
        {
            try
            {
                RestoreLegacy(scriptId, userKey, sitePath);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[附加配置] 现场还原失败（脚本 {scriptId} / 用户 {userKey}）：{sitePath}：{ex.Message}");
            }
        }
    }

    /// <summary>按会话标记恢复冻结的附加现场；任何失败都会抛出以保留 durable marker 并触发重试。</summary>
    public static void RestoreAll(
        string scriptId,
        string userKey,
        IReadOnlyList<ConfigSessionExtraPath> entries)
    {
        ValidateEntries(entries);
        foreach (ConfigSessionExtraPath entry in entries)
        {
            Restore(scriptId, userKey, entry);
        }
    }

    /// <summary>检测没有会话 manifest 可解释的旧 original-extra 现场；只报告，不猜测恢复路径。</summary>
    internal static bool HasUntrackedResidue(string scriptId, string userKey)
    {
        string root = ConfigSwapPaths.OriginalExtraRoot(scriptId, userKey);
        return Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any();
    }

    private static void Prepare(string scriptId, string userKey, ConfigSessionExtraPath entry)
    {
        string sitePath = entry.Path;
        string storeExtra = ConfigSwapPaths.StoreExtraDir(scriptId, userKey, sitePath);
        string originalExtra = ConfigSwapPaths.OriginalExtraDir(scriptId, userKey, sitePath);

        // 先处理同一条路径的上次残留；entry 的 OriginalKind 来自上次会话标记，不能重新猜测。
        Restore(scriptId, userKey, entry);
        PathKind expectedKind = PathKindUtil.Parse(entry.OriginalKind);
        PathKind siteKind = PathKindUtil.KindOf(sitePath);
        if (siteKind != expectedKind)
        {
            throw new IOException($"附加配置现场形态已变化，拒绝覆盖：{sitePath}（标记={entry.OriginalKind}，当前={PathKindUtil.Text(siteKind)}）");
        }

        bool hasStore = Directory.Exists(storeExtra) && Directory.EnumerateFileSystemEntries(storeExtra).Any();
        if (!hasStore && siteKind != PathKind.Missing)
        {
            ConfigSwapPrimitives.ClearPath(storeExtra, PathKindUtil.KindOf(storeExtra));
            ConfigSwapPrimitives.CopyAs(sitePath, storeExtra, PathKind.Dir);
            hasStore = Directory.Exists(storeExtra) && Directory.EnumerateFileSystemEntries(storeExtra).Any();
            Audit.Log(Audit.System, "附加配置建立快照", $"脚本 {scriptId} / 用户 {userKey}：{sitePath}");
        }
        if (siteKind == PathKind.Missing)
        {
            if (hasStore)
            {
                CopyStoreToSite(storeExtra, sitePath, InferKind(sitePath));
            }
            return;
        }

        ConfigSwapPrimitives.ClearPath(originalExtra, PathKindUtil.KindOf(originalExtra));
        DeleteKindMark(scriptId, userKey, sitePath);
        ConfigSwapPrimitives.MoveAs(sitePath, originalExtra, PathKind.Dir);
        // sidecar 继续服务于 v0.14.1/v0.14.2 遗留恢复；新会话同时由 .session 的 OriginalKind 保护。
        WriteKindMark(scriptId, userKey, sitePath, siteKind);
        if (hasStore)
        {
            CopyStoreToSite(storeExtra, sitePath, siteKind);
        }
    }

    private static void PrepareFirstEdit(string scriptId, string userKey, ConfigSessionExtraPath entry)
    {
        string sitePath = entry.Path;
        PathKind expectedKind = PathKindUtil.Parse(entry.OriginalKind);
        PathKind siteKind = PathKindUtil.KindOf(sitePath);
        Restore(scriptId, userKey, entry);
        siteKind = PathKindUtil.KindOf(sitePath);
        if (siteKind != expectedKind)
        {
            throw new IOException($"首次编辑附加配置现场形态已变化，拒绝覆盖：{sitePath}（标记={entry.OriginalKind}，当前={PathKindUtil.Text(siteKind)}）");
        }
        if (siteKind == PathKind.Missing)
        {
            return;
        }

        string originalExtra = ConfigSwapPaths.OriginalExtraDir(scriptId, userKey, sitePath);
        ConfigSwapPrimitives.ClearPath(originalExtra, PathKindUtil.KindOf(originalExtra));
        DeleteKindMark(scriptId, userKey, sitePath);
        ConfigSwapPrimitives.MoveAs(sitePath, originalExtra, PathKind.Dir);
        WriteKindMark(scriptId, userKey, sitePath, siteKind);
        ConfigSwapPrimitives.CopyAs(originalExtra, sitePath, siteKind);
    }

    private static void SyncFromSite(string scriptId, string userKey, string sitePath, string phase)
    {
        string storeExtra = ConfigSwapPaths.StoreExtraDir(scriptId, userKey, sitePath);
        ExtraConfigStoreTransaction.Recover(scriptId, userKey, sitePath);
        if (!ConfigSwapSession.ValidForSync(sitePath, storeExtra))
        {
            return;
        }
        if (!ConfigSwapSession.StableConfig(sitePath))
        {
            Logger.Warn($"[附加配置] 跳过同步：现场仍在变化（脚本 {scriptId} / 用户 {userKey}，{phase}）。");
            return;
        }
        string stableSample = ConfigSwapSession.SampleConfig(sitePath);
        ConfigStoreDiffPlan plan = ConfigStoreDiff.Build(
            sitePath,
            storeExtra,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            null);
        if (!plan.HasChanges)
        {
            return;
        }
        ExtraConfigStoreTransaction.Apply(
            scriptId,
            userKey,
            sitePath,
            storeExtra,
            plan,
            stableSample);
        Audit.Log(
            Audit.System,
            "附加配置同步",
            $"脚本 {scriptId} / 用户 {userKey}（{phase}，新增 {plan.Added.Count}，变更 {plan.Changed.Count}，删除 {plan.Deleted.Count}）");
    }

    private static void Restore(string scriptId, string userKey, ConfigSessionExtraPath entry)
    {
        string sitePath = entry.Path;
        string originalExtra = ConfigSwapPaths.OriginalExtraDir(scriptId, userKey, sitePath);
        // original-extra 本身就是备份容器；空目录也代表原现场确实存在，不能用“有子项”判断。
        bool hasOriginal = Directory.Exists(originalExtra);
        PathKind originalKind = PathKindUtil.Parse(entry.OriginalKind);
        if (hasOriginal)
        {
            if (originalKind == PathKind.Missing)
            {
                throw new IOException($"附加配置恢复标记形态无效：{sitePath}");
            }
            ConfigSwapPrimitives.ClearPath(sitePath, PathKindUtil.KindOf(sitePath));
            ConfigSwapPrimitives.CopyAs(originalExtra, sitePath, originalKind);
            ConfigSwapPrimitives.ClearPath(originalExtra, PathKind.Dir);
            DeleteKindMark(scriptId, userKey, sitePath);
            return;
        }

        // 原现场本来不存在时，Prepare 可能已经把 store 物化到了 site；恢复必须清回 Missing。
        if (originalKind == PathKind.Missing)
        {
            ConfigSwapPrimitives.ClearPath(sitePath, PathKindUtil.KindOf(sitePath));
            DeleteKindMark(scriptId, userKey, sitePath);
        }
    }

    private static void RestoreLegacy(string scriptId, string userKey, string sitePath)
    {
        string originalExtra = ConfigSwapPaths.OriginalExtraDir(scriptId, userKey, sitePath);
        if (!Directory.Exists(originalExtra))
        {
            return;
        }
        PathKind? markedKind = ReadKindMark(scriptId, userKey, sitePath);
        if (markedKind is null)
        {
            // 旧现场缺少形态 sidecar 时，扩展名推断不足以证明原配置归属，保留现场等待人工核查。
            Logger.Warn($"[附加配置] 旧现场缺少可靠形态标记，保留 original-extra：{originalExtra}");
            return;
        }
        PathKind kind = markedKind.Value;
        ConfigSwapPrimitives.ClearPath(sitePath, PathKindUtil.KindOf(sitePath));
        ConfigSwapPrimitives.CopyAs(originalExtra, sitePath, kind);
        ConfigSwapPrimitives.ClearPath(originalExtra, PathKind.Dir);
        DeleteKindMark(scriptId, userKey, sitePath);
    }

    private static void CopyStoreToSite(string storeExtra, string sitePath, PathKind kind)
    {
        ConfigSwapPrimitives.ClearPath(sitePath, PathKindUtil.KindOf(sitePath));
        ConfigSwapPrimitives.CopyAs(storeExtra, sitePath, kind);
    }

    /// <summary>现场形态推断：有扩展名按文件、无扩展名按目录。</summary>
    private static PathKind InferKind(string sitePath)
    {
        return string.IsNullOrWhiteSpace(Path.GetExtension(sitePath)) ? PathKind.Dir : PathKind.File;
    }

    private static string KindMarkPath(string scriptId, string userKey, string sitePath)
    {
        return ConfigSwapPaths.OriginalExtraDir(scriptId, userKey, sitePath) + ".kind";
    }

    private static void WriteKindMark(string scriptId, string userKey, string sitePath, PathKind kind)
    {
        string path = KindMarkPath(scriptId, userKey, sitePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, PathKindUtil.Text(kind));
    }

    private static PathKind? ReadKindMark(string scriptId, string userKey, string sitePath)
    {
        string path = KindMarkPath(scriptId, userKey, sitePath);
        if (!File.Exists(path))
        {
            return null;
        }
        return File.ReadAllText(path).Trim() switch
        {
            "file" => PathKind.File,
            "dir" => PathKind.Dir,
            _ => null,
        };
    }

    private static void DeleteKindMark(string scriptId, string userKey, string sitePath)
    {
        string path = KindMarkPath(scriptId, userKey, sitePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void ValidateEntries(IReadOnlyList<ConfigSessionExtraPath> entries)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ConfigSessionExtraPath entry in entries)
        {
            if (entry is null
                || string.IsNullOrWhiteSpace(entry.Path)
                || !Path.IsPathRooted(entry.Path)
                || entry.OriginalKind is not ("missing" or "file" or "dir")
                || !paths.Add(Path.GetFullPath(entry.Path)))
            {
                throw new InvalidDataException("附加配置会话路径清单无效");
            }
        }
    }

}
