using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Configuration;

/// <summary>
/// 附加配置路径（专项插件 resolve.json extraConfigPaths）的轻量同步层。
/// 与主配置的会话标记/增量事务体系分离：所有动作幂等，失败仅记录日志、不阻断主配置流程；
/// 快照目录按声明路径短哈希定位（ConfigSwapPaths.ExtraKey），与声明顺序无关，声明路径变更即视为
/// 新快照并自动采用现场初始化。全部方法要求调用方已持有该脚本的配置交换锁（ConfigSwapPrimitives.WithSwapLock）。
/// </summary>
internal static class ExtraConfigSync
{
    /// <summary>运行/编辑开始前的现场准备。每条附加路径依次：
    /// ① 还原上次会话中断残留的现场备份（自愈）；② 快照为空且现场存在时把现场复制为初始快照（复用语义）；
    /// ③ 现场移入 original-extra 备份区（记录形态标记）；④ 快照非空时以快照覆盖现场。</summary>
    public static void PrepareAll(string scriptId, string userKey, IReadOnlyList<string> sitePaths)
    {
        if (sitePaths.Count == 0)
        {
            return;
        }
        foreach (string sitePath in sitePaths)
        {
            Prepare(scriptId, userKey, sitePath);
        }
    }

    /// <summary>现场内容差异同步入快照（运行中首查/收尾同步、编辑提交共用）。现场缺失或校验不过时跳过。</summary>
    public static void SyncAllFromSite(string scriptId, string userKey, IReadOnlyList<string> sitePaths, string phase)
    {
        foreach (string sitePath in sitePaths)
        {
            SyncFromSite(scriptId, userKey, sitePath, phase);
        }
    }

    /// <summary>运行/编辑结束后的现场还原：清掉运行产物现场，original-extra 备份按形态标记还原。</summary>
    public static void RestoreAll(string scriptId, string userKey, IReadOnlyList<string> sitePaths)
    {
        foreach (string sitePath in sitePaths)
        {
            Restore(scriptId, userKey, sitePath);
        }
    }

    private static void Prepare(string scriptId, string userKey, string sitePath)
    {
        string storeExtra = ConfigSwapPaths.StoreExtraDir(scriptId, userKey, sitePath);
        string originalExtra = ConfigSwapPaths.OriginalExtraDir(scriptId, userKey, sitePath);
        try
        {
            Restore(scriptId, userKey, sitePath);
            bool hasStore = Directory.Exists(storeExtra) && Directory.EnumerateFileSystemEntries(storeExtra).Any();
            PathKind siteKind = PathKindUtil.KindOf(sitePath);
            if (!hasStore && siteKind != PathKind.Missing)
            {
                ConfigSwapPrimitives.ClearPath(storeExtra, PathKindUtil.KindOf(storeExtra));
                ConfigSwapPrimitives.CopyAs(sitePath, storeExtra, PathKind.Dir);
                hasStore = true;
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
            WriteKindMark(scriptId, userKey, sitePath, siteKind);
            if (hasStore)
            {
                CopyStoreToSite(storeExtra, sitePath, siteKind);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[附加配置] 准备失败（脚本 {scriptId} / 用户 {userKey}）：{sitePath}：{ex.Message}");
        }
    }

    private static void SyncFromSite(string scriptId, string userKey, string sitePath, string phase)
    {
        string storeExtra = ConfigSwapPaths.StoreExtraDir(scriptId, userKey, sitePath);
        try
        {
            if (!ConfigSwapSession.ValidForSync(sitePath, storeExtra))
            {
                return;
            }
            if (!ConfigSwapSession.StableConfig(sitePath))
            {
                Logger.Warn($"[附加配置] 跳过同步：现场仍在变化（脚本 {scriptId} / 用户 {userKey}，{phase}）。");
                return;
            }
            ConfigStoreDiffPlan plan = ConfigStoreDiff.Build(sitePath, storeExtra, new HashSet<string>(StringComparer.OrdinalIgnoreCase), null);
            if (!plan.HasChanges)
            {
                return;
            }
            foreach (string relative in plan.Added.Concat(plan.Changed))
            {
                string source = ResolveWithin(sitePath, relative);
                string destination = ResolveWithin(storeExtra, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
            foreach (string relative in plan.Deleted)
            {
                string destination = ResolveWithin(storeExtra, relative);
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
            }
            Audit.Log(Audit.System, "附加配置同步", $"脚本 {scriptId} / 用户 {userKey}（{phase}，新增 {plan.Added.Count}，变更 {plan.Changed.Count}，删除 {plan.Deleted.Count}）");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[附加配置] 同步失败（脚本 {scriptId} / 用户 {userKey}）：{sitePath}：{ex.Message}");
        }
    }

    private static void Restore(string scriptId, string userKey, string sitePath)
    {
        string originalExtra = ConfigSwapPaths.OriginalExtraDir(scriptId, userKey, sitePath);
        try
        {
            if (!Directory.Exists(originalExtra) || !Directory.EnumerateFileSystemEntries(originalExtra).Any())
            {
                return;
            }
            PathKind siteKind = ReadKindMark(scriptId, userKey, sitePath) ?? InferKind(sitePath);
            ConfigSwapPrimitives.ClearPath(sitePath, PathKindUtil.KindOf(sitePath));
            ConfigSwapPrimitives.CopyAs(originalExtra, sitePath, siteKind);
            ConfigSwapPrimitives.TryDeleteDir(originalExtra);
            DeleteKindMark(scriptId, userKey, sitePath);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[附加配置] 现场还原失败（脚本 {scriptId} / 用户 {userKey}）：{sitePath}：{ex.Message}");
        }
    }

    private static void CopyStoreToSite(string storeExtra, string sitePath, PathKind kind)
    {
        ConfigSwapPrimitives.ClearPath(sitePath, PathKindUtil.KindOf(sitePath));
        ConfigSwapPrimitives.CopyAs(storeExtra, sitePath, kind);
    }

    /// <summary>现场形态推断：有扩展名按文件、无扩展名按目录（与主配置 RestoreKind 的 Missing 推断一致）。</summary>
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
        try
        {
            string path = KindMarkPath(scriptId, userKey, sitePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, PathKindUtil.Text(kind));
        }
        catch (Exception ex)
        {
            Logger.Warn($"[附加配置] 写入形态标记失败（{sitePath}）：{ex.Message}");
        }
    }

    private static PathKind? ReadKindMark(string scriptId, string userKey, string sitePath)
    {
        try
        {
            string path = KindMarkPath(scriptId, userKey, sitePath);
            return File.Exists(path) ? PathKindUtil.Parse(File.ReadAllText(path).Trim()) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void DeleteKindMark(string scriptId, string userKey, string sitePath)
    {
        try
        {
            string path = KindMarkPath(scriptId, userKey, sitePath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string ResolveWithin(string root, string relative)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, ConfigStoreDiff.NormalizeRelative(relative)));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"附加配置路径越界：{relative}");
        }
        return full;
    }
}
