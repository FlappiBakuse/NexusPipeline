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
    /* ---------------- 自动更新配置：config → store 增量事务同步 ---------------- */

    /// <summary>自动更新配置同步：把运行生效的 config 变更增量提交到用户快照 store。
    /// 插队文件（swap-backup/.meta 清单内）有还原描述（script/config-restore.json）时先还原启停字段再写入；
    /// 无还原描述时跳过（store 保持原样）。失败仅告警不阻断运行收尾；不改 .session 标记、不清 swap-backup。</summary>
    public static void SyncConfigToStore(string scriptId, string userName, string configPath, bool firstCheck)
    {
        try
        {
            ConfigSwapPrimitives.WithSwapLock(scriptId, () =>
            {
                // 1. 会话有效性：同步仅当运行交换会话仍处于活动状态（防 15s 首次检测与收尾还原的时序异常）。
                ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
                if (mark is null || !string.Equals(mark.SessionPhase, "run", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn($"[配置同步] 跳过：脚本「{scriptId}」用户「{userName}」无进行中的运行会话（{SyncPhaseText(firstCheck)}）。");
                    return;
                }
                string store = ConfigPaths.StoreDir(scriptId, userName);
                // 2. 基础有效性校验：config 缺失/为空/文件数骤降 → 跳过（防坏态入库永久污染快照）。
                if (!ValidForSync(configPath, store)) return;
                // 3. 稳定性检查（评估后扩展为全部同步）：短间隔两次采样不一致 = 脚本（含外部守护进程）
                // 仍在写配置 → 跳过本次，保留旧快照（收尾同步同样执行——进程确认退出后仍不一致说明有
                // 外部写入者在半写，此时入库存在污染风险）。
                if (!StableConfig(configPath))
                {
                    Logger.Warn($"[配置同步] 跳过：脚本「{scriptId}」用户「{userName}」配置仍在变化（{SyncPhaseText(firstCheck)}，保留旧快照）。");
                    return;
                }
                string stableSample = SampleConfig(configPath);
                // 4. 插队清单 + 还原描述。
                HashSet<string> swapFiles = ReadSwapFiles(ConfigPaths.ReplaceBackupDir(scriptId, userName));
                ConfigRestoreDescriptor? descriptor = ReadRestoreDescriptor(ConfigPaths.ScriptDir(scriptId, userName));
                // 5. 只暂存新增/变更文件，并为替换/删除文件保存回滚副本；源配置变化则整次放弃。
                ConfigStoreTransactionResult result = ConfigStoreTransaction.Apply(
                    scriptId,
                    userName,
                    configPath,
                    swapFiles,
                    descriptor,
                    stableSample,
                    mark);
                Audit.Log(Audit.System, "自动更新配置", $"{scriptId} / {userName} → store（新增 {result.Added}，变更 {result.Changed}，删除 {result.Deleted}，保留 {result.Preserved}，{SyncPhaseText(firstCheck)}）");
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"[配置同步] 失败（脚本「{scriptId}」用户「{userName}」）：{ex.Message}");
        }
    }

    /// <summary>
    /// 失败重试前复用当前活动配置；original 继续保存运行前现场，当前 config 直接作为下一轮输入。
    /// </summary>
    public static string? PrepareForRetry(string scriptId, string userName, string configPath)
    {
        string? error = null;
        try
        {
            ConfigSwapPrimitives.WithSwapLock(scriptId, () =>
            {
                ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
                if (mark is null || !string.Equals(mark.SessionPhase, "run", StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("未找到有效的运行配置交换会话");
                }
                if (File.Exists(ConfigPaths.StoreTransactionBlockedPath(scriptId, userName)))
                {
                    throw new IOException($"配置快照事务已被阻断，请先处理现场：{ConfigPaths.StoreTransactionBlockedPath(scriptId, userName)}");
                }
                if (PathKindUtil.KindOf(configPath) == PathKind.Missing)
                {
                    throw new IOException("当前活动配置不存在，无法安全复用进行重试");
                }
                Logger.Info($"[配置交换] 脚本「{scriptId}」用户「{userName}」已重新准备重试配置。");
            });
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Logger.Error($"[错误] 重新准备重试配置失败（脚本 {scriptId} / 用户 {userName}）：{error}");
        }
        return error;
    }

    private static string SyncPhaseText(bool firstCheck)
    {
        return firstCheck ? "首次检测" : "运行收尾";
    }

    /// <summary>基础有效性校验：config 缺失/为空/文件数骤降/JSON 型内容损坏时跳过同步（防脚本写坏/清空中被入库）。</summary>
    internal static bool ValidForSync(string configPath, string store)
    {
        PathKind kind = PathKindUtil.KindOf(configPath);
        if (kind == PathKind.Missing)
        {
            Logger.Warn($"[配置同步] 跳过：配置位置不存在（{configPath}）。");
            return false;
        }
        if (kind == PathKind.File)
        {
            if (new FileInfo(configPath).Length == 0)
            {
                Logger.Warn($"[配置同步] 跳过：配置文件为空（{configPath}）。");
                return false;
            }
            if (!ContentValidForSync(configPath, kind))
            {
                Logger.Warn($"[配置同步] 跳过：配置文件内容有效性校验失败（{configPath}），疑似写入中断/半写，保留旧快照。");
                return false;
            }
            return true;
        }
        string[] files = Directory.GetFiles(configPath, "*", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            Logger.Warn($"[配置同步] 跳过：配置目录为空（{configPath}）。");
            return false;
        }
        if (Directory.Exists(store))
        {
            int storeCount = Directory.GetFiles(store, "*", SearchOption.AllDirectories).Length;
            if (storeCount > 0 && files.Length * 2 <= storeCount)
            {
                Logger.Warn($"[配置同步] 跳过：配置文件数骤降（config {files.Length} < store {storeCount} 一半），疑似脚本写坏/清空中，保留旧快照。");
                return false;
            }
        }
        if (!ContentValidForSync(configPath, kind))
        {
            Logger.Warn($"[配置同步] 跳过：配置目录内容有效性校验失败（{configPath}），疑似脚本写入中断/半写，保留旧快照。");
            return false;
        }
        return true;
    }

    /// <summary>内容有效性探测：JSON 型文件（.json 扩展名或明确 JSON 内容）必须可解析；
    /// 非 JSON 型文本不校验。大文件不再直接视为有效，避免半写内容绕过保护。
    /// 只读探测不重写；解析失败视为写入中断（脚本被杀瞬间半写），跳过同步保留旧快照。</summary>
    internal static bool ContentValidForSync(string configPath, PathKind kind)
    {
        try
        {
            if (kind == PathKind.File)
            {
                return JsonContentValid(configPath);
            }
            foreach (string file in Directory.GetFiles(configPath, "*", SearchOption.AllDirectories))
            {
                if (!JsonContentValid(file))
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[配置同步] 内容有效性探测失败（{configPath}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>单个文件是否 JSON 型且可解析：满足型条件（.json 扩展名或内容以 {/[ 开头）但解析失败 = 损坏；
    /// 非 JSON 型一律通过。0 字节 .json = 半写坏态；0 字节其他扩展名不校验。</summary>
    private static bool JsonContentValid(string path)
    {
        var info = new FileInfo(path);
        bool jsonExt = string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);
        if (info.Length == 0)
        {
            return !jsonExt;
        }
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception)
        {
            return false;
        }
        string trimmed = text.TrimStart(' ', '\t', '\r', '\n', '\uFEFF');
        bool jsonLike = jsonExt || trimmed.StartsWith('{') || IsJsonArrayLike(trimmed);
        if (!jsonLike)
        {
            return true;
        }
        try
        {
            return JsonNode.Parse(text) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsJsonArrayLike(string text)
    {
        if (text.Length == 0 || text[0] != '[')
        {
            return false;
        }
        string rest = text[1..].TrimStart();
        return rest.Length == 0 || "[{\"-0123456789tfn".Contains(rest[0]);
    }

    /// <summary>稳定性检查：短间隔两次采样 config 文件清单/长度/修改时间，不一致视为仍在写入。</summary>
    internal static bool StableConfig(string configPath)
    {
        string first = SampleConfig(configPath);
        Thread.Sleep(TestHooks.ScaledMs(800));
        string second = SampleConfig(configPath);
        return string.Equals(first, second, StringComparison.Ordinal);
    }

    internal static string SampleConfig(string configPath)
    {
        PathKind kind = PathKindUtil.KindOf(configPath);
        if (kind == PathKind.File)
        {
            var info = new FileInfo(configPath);
            return $"F|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        var sb = new StringBuilder();
        foreach (string file in Directory.GetFiles(configPath, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            sb.Append(Path.GetRelativePath(configPath, file)).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }
        return sb.ToString();
    }

}
