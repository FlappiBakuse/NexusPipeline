using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>配置交换会话标记：交换开始写入、完成删除；崩溃后可据此恢复（安全优先：原配置必还原）。</summary>
internal sealed class ConfigSessionMark
{
    public string ScriptId { get; set; } = "";

    public string UserName { get; set; } = "";

    public string ConfigPath { get; set; } = "";

    public string OriginalKind { get; set; } = "missing";

    public string Phase { get; set; } = "run";

    public DateTime StartedAt { get; set; } = DateTime.Now;

    /// <summary>本次编辑会话由宿主生成了配置模板（重启恢复时清理 config 位置的编辑产物，还原编辑前状态）。</summary>
    public bool GeneratedTemplate { get; set; }

    /// <summary>模板目录复制生成的文件清单（相对 configPath 父目录，v0.6.3+ 模板目录形态；cancel/重启恢复按清单精确清理）。</summary>
    public List<string> TemplateFiles { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        // v0.7.5（KN-55）：写盘改 PascalCase（与「磁盘 JSON = PascalCase」约定一致）；PropertyNameCaseInsensitive
        // 兼容读取旧版 camelCase 标记（旧版本崩溃现场仍可完整恢复，无需迁移）。
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string MarkFile(string scriptId, string userName)
    {
        return Path.Combine(AppPaths.DataDir, scriptId, userName, ".session");
    }

    public void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkFile(ScriptId, UserName))!);
        JsonUtil.WriteAtomic(MarkFile(ScriptId, UserName), JsonSerializer.Serialize(this, Options));
    }

    public static ConfigSessionMark? TryRead(string scriptId, string userName)
    {
        string file = MarkFile(scriptId, userName);
        if (!File.Exists(file))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ConfigSessionMark>(File.ReadAllText(file), Options);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 读取配置会话标记失败（{file}）：{ex.Message}");
            return null;
        }
    }

    public static void Clear(string scriptId, string userName)
    {
        try
        {
            File.Delete(MarkFile(scriptId, userName));
        }
        catch
        {
        }
    }
}

/// <summary>编辑配置会话（WebServer 持有的进程句柄与标记）。</summary>
internal sealed class EditSession
{
    public required ScriptInstance Script { get; init; }

    public required ScriptUser User { get; init; }

    public Process? Process { get; set; }

    public ConfigSessionMark Mark { get; init; } = new();

    /// <summary>本次会话由宿主生成了配置模板（cancel 时需清理生成文件）。</summary>
    public bool GeneratedConfigTemplate { get; set; }
}

/// <summary>
/// 配置交换会话/恢复层（v0.5.0 从 UserConfigManager 拆出）：配置替换（replaceConfigs）、
/// .session 标记、操作前自愈 + 启动扫描恢复 + 后台延迟重试。文件原语见 <see cref="ConfigSwapPrimitives"/>，
/// 数据目录定位见 <see cref="ConfigSwapPaths"/>。
/// </summary>
internal static class ConfigSwapSession
{
    /* ---------------- 配置替换（插队替换 replaceConfigs） ---------------- */

    /// <summary>解析配置替换目标：config 为单文件时仅允许替换该文件本身（rel 须等于文件名，忽略大小写），目录时走相对路径边界解析。</summary>
    private static string? ResolveConfigTarget(string configPath, string rel)
    {
        if (File.Exists(configPath))
        {
            return string.Equals(Path.GetFileName(configPath), rel, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(configPath)
                : null;
        }
        return JudgeScriptRunner.ResolveWithin(configPath, rel);
    }

    /// <summary>应用配置替换：把 script 目录内文件复制覆盖到 config 对应位置；首次替换前备份原始内容到 swap-backup（含 .meta 记录 configPath 与新增文件清单）。</summary>
    public static string? ApplyConfigReplacements(string scriptId, string? userName, string configPath, List<string> replacements)
    {
        string scriptDir = ConfigSwapPaths.ScriptDir(scriptId, userName);
        string backupDir = ConfigSwapPaths.ReplaceBackupDir(scriptId, userName);
        string metaPath = Path.Combine(backupDir, ".meta");
        List<string> newFiles = ReadMetaNewFiles(metaPath);
        foreach (string rel in replacements)
        {
            string? source = JudgeScriptRunner.ResolveWithin(scriptDir, rel);
            if (source is null || !File.Exists(source))
            {
                Logger.Warn($"[警告] 配置替换源文件无效（{rel}），跳过");
                continue;
            }
            string? target = ResolveConfigTarget(configPath, rel);
            if (target is null)
            {
                Logger.Warn($"[警告] 配置替换目标越界（{rel}），跳过");
                continue;
            }
            try
            {
                if (File.Exists(target))
                {
                    if (!newFiles.Contains(rel, StringComparer.OrdinalIgnoreCase))
                    {
                        string backupFile = Path.Combine(backupDir, rel);
                        if (!File.Exists(backupFile))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                            File.Copy(target, backupFile, overwrite: true);
                        }
                    }
                }
                else
                {
                    newFiles.Add(rel);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
                Logger.Info($"[配置替换] 脚本「{scriptId}」已替换配置：{target} ← {rel}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 配置替换失败（{rel}）：{ex.Message}");
            }
        }
        if (newFiles.Count > 0 || (Directory.Exists(backupDir) && Directory.EnumerateFileSystemEntries(backupDir).Any()))
        {
            Directory.CreateDirectory(backupDir);
            // v0.7.5（KN-55）：.meta 写盘改 PascalCase（与「磁盘 JSON = PascalCase」约定一致）；读取侧兼容旧版 camelCase。
            JsonUtil.WriteAtomic(metaPath, JsonSerializer.Serialize(new { ConfigPath = configPath, NewFiles = newFiles }));
        }
        return null;
    }

    /// <summary>读取 .meta 中记录的新增文件清单（多轮替换累积）。</summary>
    private static List<string> ReadMetaNewFiles(string metaPath)
    {
        var list = new List<string>();
        if (!File.Exists(metaPath))
        {
            return list;
        }
        try
        {
            JsonNode? node = JsonNode.Parse(File.ReadAllText(metaPath));
            // v0.7.5（KN-55）：兼容旧版 camelCase 键（旧版本崩溃现场）。
            JsonArray? files = node?["NewFiles"] as JsonArray ?? node?["newFiles"] as JsonArray;
            if (files is not null)
            {
                foreach (JsonNode? item in files)
                {
                    string? text = item?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        list.Add(text);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($".meta 替换清单解析失败（{metaPath}），新增文件可能无法清理：{ex.Message}");
        }
        return list;
    }

    /// <summary>还原配置替换：成功时清理备份，任一文件失败时保留备份现场供恢复循环继续处理。</summary>
    public static bool RestoreConfigReplacements(string scriptId, string? userName)
    {
        string backupDir = ConfigSwapPaths.ReplaceBackupDir(scriptId, userName);
        if (!Directory.Exists(backupDir))
        {
            return true;
        }
        string metaPath = Path.Combine(backupDir, ".meta");
        string? configPath = null;
        var newFiles = new List<string>();
        if (File.Exists(metaPath))
        {
            try
            {
                JsonNode? node = JsonNode.Parse(File.ReadAllText(metaPath));
                // v0.7.5（KN-55）：兼容旧版 camelCase 键（旧版本崩溃现场）。
                configPath = node?["ConfigPath"]?.ToString() ?? node?["configPath"]?.ToString();
                JsonArray? metaFiles = node?["NewFiles"] as JsonArray ?? node?["newFiles"] as JsonArray;
                if (metaFiles is not null)
                {
                    foreach (JsonNode? item in metaFiles)
                    {
                        string? text = item?.ToString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            newFiles.Add(text);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[错误] 配置替换备份清单损坏（{metaPath}）：{ex.Message}");
                QuarantineInvalidBackup(backupDir, "meta 损坏");
                return false;
            }
        }
        if (string.IsNullOrWhiteSpace(configPath))
        {
            Logger.Error($"[错误] 配置替换备份清单缺少 configPath（{metaPath}）。");
            QuarantineInvalidBackup(backupDir, "缺少 configPath");
            return false;
        }
        bool restored = true;
        foreach (string file in Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(backupDir, file);
            if (rel.Equals(".meta", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string? target = ResolveConfigTarget(configPath, rel);
            if (target is null)
            {
                restored = false;
                continue;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
            catch (Exception ex)
            {
                restored = false;
                Logger.Warn($"[警告] 还原配置替换失败（{target}）：{ex.Message}");
            }
        }
        foreach (string rel in newFiles)
        {
            string? target = ResolveConfigTarget(configPath, rel);
            if (target is null || !File.Exists(target))
            {
                if (target is null)
                {
                    restored = false;
                }
                continue;
            }
            try
            {
                File.Delete(target);
                Logger.Info($"[配置替换] 已清理替换新增文件：{target}");
            }
            catch (Exception ex)
            {
                restored = false;
                Logger.Warn($"[警告] 清理替换新增文件失败（{target}）：{ex.Message}");
            }
        }
        if (!restored)
        {
            return false;
        }
        try
        {
            Directory.Delete(backupDir, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 清理配置替换备份失败，保留备份现场：{backupDir}（{ex.Message}）");
            return false;
        }
    }

    /// <summary>隔离无法安全解释的替换备份，保留现场但移出自动恢复扫描，避免每 10 秒永久重试。</summary>
    private static void QuarantineInvalidBackup(string backupDir, string reason)
    {
        string suffix = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
        string target = backupDir + ".corrupt-" + suffix;
        if (Directory.Exists(target))
        {
            target = backupDir + ".corrupt-" + suffix + "-" + Guid.NewGuid().ToString("N")[..8];
        }
        try
        {
            Directory.Move(backupDir, target);
            Logger.Error($"[恢复] 已隔离损坏的配置替换备份（{reason}）：{target}；请人工核查后处理。");
        }
        catch (Exception ex)
        {
            Logger.Error($"[恢复] 隔离损坏的配置替换备份失败（{backupDir}）：{ex.Message}；将继续保留现场。");
        }
    }

    /* ---------------- 自动更新配置（v0.7.6）：config → store 全量镜像同步 ---------------- */

    /// <summary>自动更新配置同步（v0.7.6）：把运行生效的 config 当前内容全量镜像到用户快照 store。
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
                if (mark is null || !string.Equals(mark.Phase, "run", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn($"[配置同步] 跳过：脚本「{scriptId}」用户「{userName}」无进行中的运行会话（{SyncPhaseText(firstCheck)}）。");
                    return;
                }
                string store = ConfigSwapPaths.StoreDir(scriptId, userName);
                // 2. 基础有效性校验：config 缺失/为空/文件数骤降 → 跳过（防坏态入库永久污染快照）。
                if (!ValidForSync(configPath, store)) return;
                // 3. 稳定性检查（v0.7.6 评估后扩展为全部同步）：短间隔两次采样不一致 = 脚本（含外部守护进程）
                //    仍在写配置 → 跳过本次，保留旧快照（收尾同步同样执行——进程确认退出后仍不一致说明有
                //    外部写入者在半写，此时入库存在污染风险）。
                if (!StableConfig(configPath))
                {
                    Logger.Warn($"[配置同步] 跳过：脚本「{scriptId}」用户「{userName}」配置仍在变化（{SyncPhaseText(firstCheck)}，保留旧快照）。");
                    return;
                }
                string stableSample = SampleConfig(configPath);
                // 4. 插队清单 + 还原描述。
                HashSet<string> swapFiles = ReadSwapFiles(ConfigSwapPaths.ReplaceBackupDir(scriptId, userName));
                ConfigRestoreDescriptor? descriptor = ReadRestoreDescriptor(ConfigSwapPaths.ScriptDir(scriptId, userName));
                // 5. 全量镜像到临时目录并原子替换，源配置在复制期间再次变化则整次放弃。
                (int written, int preserved) = MirrorToStoreAtomic(configPath, store, swapFiles, descriptor, stableSample);
                Audit.Log(Audit.System, "自动更新配置", $"{scriptId} / {userName} → store（写入 {written}，保留插队 {preserved}，{SyncPhaseText(firstCheck)}）");
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"[配置同步] 失败（脚本「{scriptId}」用户「{userName}」）：{ex.Message}");
        }
    }

    /// <summary>
    /// 失败重试前重新执行一次完整配置交换：当前尝试的最终 config 先保存到 retry-store，
    /// 再把 original 现场恢复并重新移入 original，最后从 retry-store 复制下一轮配置到 config。
    /// retry-store 只服务本次运行，不等同于用户永久快照 store。
    /// </summary>
    public static string? PrepareForRetry(string scriptId, string userName, string configPath)
    {
        string? error = null;
        try
        {
            ConfigSwapPrimitives.WithSwapLock(scriptId, () =>
            {
                ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
                if (mark is null || !string.Equals(mark.Phase, "run", StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("未找到有效的运行配置交换会话");
                }
                string cache = ConfigSwapPaths.CacheDir(scriptId, userName);
                string retryStore = ConfigSwapPaths.RetryStoreDir(scriptId, userName);
                PathKind currentKind = PathKindUtil.KindOf(configPath);
                ConfigSwapPrimitives.ClearPath(retryStore, PathKindUtil.KindOf(retryStore));
                if (currentKind != PathKind.Missing)
                {
                    ConfigSwapPrimitives.CopyAs(configPath, retryStore, PathKind.Dir);
                }

                if (Directory.Exists(cache) && Directory.EnumerateFileSystemEntries(cache).Any())
                {
                    ConfigSwapPrimitives.ClearPath(configPath, currentKind);
                    ConfigSwapPrimitives.MoveAs(cache, configPath, ConfigSwapPrimitives.RestoreKind(mark));
                    ConfigSwapPrimitives.ClearPath(cache, PathKindUtil.KindOf(cache));
                    ConfigSwapPrimitives.MoveAs(configPath, cache, PathKind.Dir);
                }
                else
                {
                    ConfigSwapPrimitives.ClearPath(configPath, currentKind);
                }

                if (Directory.Exists(retryStore) && Directory.EnumerateFileSystemEntries(retryStore).Any())
                {
                    ConfigSwapPrimitives.CopyAs(retryStore, configPath, ConfigSwapPrimitives.RestoreKind(mark));
                }
                else if (PathKindUtil.Parse(mark.OriginalKind) == PathKind.Dir)
                {
                    Directory.CreateDirectory(configPath);
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

    /// <summary>内容有效性探测（v0.7.8）：JSON 型文件（.json 扩展名或明确 JSON 内容）必须可解析；
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

    /// <summary>读取插队文件清单：swap-backup 内备份的被替换文件（排除 .meta）+ .meta 记录的新增文件。相对 config 定位。</summary>
    internal static HashSet<string> ReadSwapFiles(string backupDir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(backupDir))
        {
            foreach (string file in Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(backupDir, file);
                if (!rel.Equals(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    set.Add(NormalizeRelative(rel));
                }
            }
            foreach (string rel in ReadMetaNewFiles(Path.Combine(backupDir, ".meta")))
            {
                set.Add(NormalizeRelative(rel));
            }
        }
        return set;
    }

    /// <summary>还原描述：判断脚本首次触发写入 script/config-restore.json，宿主仅执行、不解析插件语义。
    /// 契约见 plugins/README.md（v0.7.6）。</summary>
    internal sealed class ConfigRestoreDescriptor
    {
        public List<FileRestore> Files { get; set; } = new();
    }

    internal sealed class FileRestore
    {
        public string File { get; set; } = "";

        public List<ToggleRestore> Toggles { get; set; } = new();
    }

    internal sealed class ToggleRestore
    {
        public string Type { get; set; } = "";

        public string Path { get; set; } = "";

        public string KeyField { get; set; } = "";

        public string EnabledField { get; set; } = "";

        public Dictionary<string, bool> Initial { get; set; } = new();
    }

    internal static ConfigRestoreDescriptor? ReadRestoreDescriptor(string scriptDir)
    {
        string file = Path.Combine(scriptDir, "config-restore.json");
        if (!File.Exists(file))
        {
            return null;
        }
        try
        {
            JsonNode? node = JsonNode.Parse(File.ReadAllText(file));
            var descriptor = new ConfigRestoreDescriptor();
            if (node?["files"] is JsonArray files)
            {
                foreach (JsonNode? item in files)
                {
                    if (item is null)
                    {
                        continue;
                    }
                    var fr = new FileRestore { File = item["file"]?.ToString() ?? "" };
                    if (item["toggles"] is JsonArray toggles)
                    {
                        foreach (JsonNode? t in toggles)
                        {
                            if (t is null)
                            {
                                continue;
                            }
                            var tr = new ToggleRestore
                            {
                                Type = t["type"]?.ToString() ?? "",
                                Path = t["path"]?.ToString() ?? "",
                                KeyField = t["keyField"]?.ToString() ?? "",
                                EnabledField = t["enabledField"]?.ToString() ?? "",
                            };
                            if (t["initial"] is JsonObject initial)
                            {
                                foreach (KeyValuePair<string, JsonNode?> kv in initial)
                                {
                                    if (kv.Value is not null && bool.TryParse(kv.Value.ToString(), out bool b))
                                    {
                                        tr.Initial[kv.Key] = b;
                                    }
                                }
                            }
                            if (!string.IsNullOrWhiteSpace(tr.Type) && !string.IsNullOrWhiteSpace(tr.Path) && tr.Initial.Count > 0)
                            {
                                fr.Toggles.Add(tr);
                            }
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(fr.File) && fr.Toggles.Count > 0)
                    {
                        descriptor.Files.Add(fr);
                    }
                }
            }
            return descriptor.Files.Count > 0 ? descriptor : null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[配置同步] 还原描述解析失败（{file}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>全量镜像（copy-then-prune）：先复制 config → store（插队文件分类处理），全部成功后再删除 store 中 config 已无的文件，
    /// 避免「先清后拷」中途失败留下空 store。</summary>
    internal static (int Written, int Deleted) MirrorToStore(string configPath, string store, HashSet<string> swapFiles, ConfigRestoreDescriptor? descriptor)
    {
        Directory.CreateDirectory(store);
        int written = 0;
        int deleted = 0;
        PathKind kind = PathKindUtil.KindOf(configPath);
        var configRels = new List<string>();
        if (kind == PathKind.File)
        {
            string rel = Path.GetFileName(configPath);
            configRels.Add(rel);
            if (CopyMirrorFile(configPath, Path.Combine(store, rel), rel, swapFiles, descriptor))
            {
                written++;
            }
        }
        else if (kind == PathKind.Dir)
        {
            foreach (string file in Directory.GetFiles(configPath, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(configPath, file);
                configRels.Add(rel);
                if (CopyMirrorFile(file, Path.Combine(store, rel), rel, swapFiles, descriptor))
                {
                    written++;
                }
            }
        }
        foreach (string file in Directory.GetFiles(store, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(store, file);
            if (configRels.Contains(rel, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[配置同步] 删除 store 多余文件失败（{file}）：{ex.Message}");
            }
        }
        return (written, deleted);
    }

    /// <summary>
    /// 事务化全量镜像：先写 store.tmp，复制期间源配置发生变化则放弃；成功后将旧 store 保留为
    /// store.previous，再把临时目录移动为 store。store.previous 用于进程崩溃后的恢复。
    /// 无还原描述的插队文件从旧 store 保留，不参与本轮内容覆盖或清理。
    /// </summary>
    internal static (int Written, int Preserved) MirrorToStoreAtomic(
        string configPath,
        string store,
        HashSet<string> swapFiles,
        ConfigRestoreDescriptor? descriptor,
        string expectedSample)
    {
        string temp = store + ".tmp";
        string previous = store + ".previous";
        int written = 0;
        int preserved = 0;
        try
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
            Directory.CreateDirectory(temp);

            PathKind kind = PathKindUtil.KindOf(configPath);
            if (kind == PathKind.Missing)
            {
                throw new IOException("配置位置在镜像期间消失");
            }
            var configRels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (kind == PathKind.File)
            {
                string rel = Path.GetFileName(configPath);
                configRels.Add(rel);
                    written += StageMirrorFile(configPath, Path.Combine(temp, rel), rel, store, temp, swapFiles, descriptor, ref preserved);
            }
            else
            {
                foreach (string source in Directory.GetFiles(configPath, "*", SearchOption.AllDirectories))
                {
                    string rel = NormalizeRelative(Path.GetRelativePath(configPath, source));
                    configRels.Add(rel);
                    written += StageMirrorFile(source, Path.Combine(temp, rel), rel, store, temp, swapFiles, descriptor, ref preserved);
                }
                if (configRels.Count == 0)
                {
                    throw new IOException("配置目录在镜像期间变为空");
                }
            }

            foreach (string rel in swapFiles)
            {
                string normalized = NormalizeRelative(rel);
                if (configRels.Contains(normalized))
                {
                    continue;
                }
                if (CopyExistingStoreFile(store, temp, normalized))
                {
                    preserved++;
                }
            }

            if (!string.Equals(expectedSample, SampleConfig(configPath), StringComparison.Ordinal))
            {
                throw new IOException("配置在镜像期间发生变化，保留旧快照");
            }
            CommitStagedStore(temp, store, previous);
            return (written, preserved);
        }
        catch
        {
            ConfigSwapPrimitives.TryDeleteDir(temp);
            throw;
        }
    }

    private static int StageMirrorFile(
        string source,
        string dest,
        string rel,
        string store,
        string stageRoot,
        HashSet<string> swapFiles,
        ConfigRestoreDescriptor? descriptor,
        ref int preserved)
    {
        if (swapFiles.Contains(rel))
        {
            FileRestore? restore = FindRestoreFile(descriptor, rel);
            if (restore is null)
            {
                Logger.Info($"[配置同步] 插队文件无还原描述，保留旧快照：{rel}");
                if (CopyExistingStoreFile(store, stageRoot, rel))
                {
                    preserved++;
                }
                return 0;
            }
            (string content, Encoding encoding) = ReadTextPreservingEncoding(source);
            foreach (ToggleRestore toggle in restore.Toggles)
            {
                if (!ApplyToggle(ref content, toggle))
                {
                    throw new IOException($"插队文件还原描述应用失败：{rel} / {toggle.Path}");
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllText(dest, content, encoding);
            return 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(source, dest, overwrite: true);
        return 1;
    }

    private static (string Content, Encoding Encoding) ReadTextPreservingEncoding(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] prefix = new byte[4];
        int read = stream.Read(prefix, 0, prefix.Length);
        bool hasBom = (read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
            || (read >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE)
            || (read >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF)
            || (read >= 4 && prefix[0] == 0xFF && prefix[1] == 0xFE && prefix[2] == 0x00 && prefix[3] == 0x00)
            || (read >= 4 && prefix[0] == 0x00 && prefix[1] == 0x00 && prefix[2] == 0xFE && prefix[3] == 0xFF);
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string content = reader.ReadToEnd();
        Encoding detected = reader.CurrentEncoding;
        Encoding output = detected.CodePage switch
        {
            65001 => new UTF8Encoding(hasBom),
            1200 => new UnicodeEncoding(false, hasBom),
            1201 => new UnicodeEncoding(true, hasBom),
            12000 => new UTF32Encoding(false, hasBom),
            12001 => new UTF32Encoding(true, hasBom),
            _ => detected,
        };
        return (content, output);
    }

    private static bool CopyExistingStoreFile(string store, string targetRoot, string rel)
    {
        string? source = JudgeScriptRunner.ResolveWithin(store, rel);
        if (source is null || !File.Exists(source))
        {
            return false;
        }
        string dest = Path.Combine(targetRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(source, dest, overwrite: true);
        return true;
    }

    private static void CommitStagedStore(string temp, string store, string previous)
    {
        if (File.Exists(store) || File.Exists(previous))
        {
            throw new IOException("用户快照路径形态不正确");
        }
        if (Directory.Exists(previous))
        {
            Directory.Delete(previous, recursive: true);
        }
        bool oldMoved = false;
        try
        {
            if (Directory.Exists(store))
            {
                Directory.Move(store, previous);
                oldMoved = true;
            }
            Directory.Move(temp, store);
        }
        catch
        {
            if (oldMoved && !Directory.Exists(store) && Directory.Exists(previous))
            {
                Directory.Move(previous, store);
            }
            throw;
        }
    }

    /// <summary>将编辑后的配置先完整复制到临时快照，再以目录事务替换用户 store。</summary>
    public static void CommitStoreSnapshot(string configPath, string store)
    {
        string temp = store + ".tmp";
        try
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
            ConfigSwapPrimitives.CopyAs(configPath, temp, PathKind.Dir);
            CommitStagedStore(temp, store, store + ".previous");
        }
        catch
        {
            ConfigSwapPrimitives.TryDeleteDir(temp);
            throw;
        }
    }

    private static FileRestore? FindRestoreFile(ConfigRestoreDescriptor? descriptor, string rel)
    {
        return descriptor?.Files.FirstOrDefault(file =>
            string.Equals(NormalizeRelative(file.File), NormalizeRelative(rel), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeRelative(string value)
    {
        return value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
    }

    /// <summary>镜像单个文件到 store：插队文件有还原描述 → 还原启停后写入；无还原描述 → 跳过（store 保留原样）；其余复制覆盖。</summary>
    internal static bool CopyMirrorFile(string source, string dest, string rel, HashSet<string> swapFiles, ConfigRestoreDescriptor? descriptor)
    {
        try
        {
            if (swapFiles.Contains(rel))
            {
                FileRestore? fr = descriptor?.Files.FirstOrDefault(f => string.Equals(f.File, rel, StringComparison.OrdinalIgnoreCase));
                if (fr is null)
                {
                    Logger.Info($"[配置同步] 插队文件无还原描述，跳过镜像：{rel}（store 保持原样）");
                    return false;
                }
                string content = File.ReadAllText(source);
                foreach (ToggleRestore toggle in fr.Toggles)
                {
                    if (!ApplyToggle(ref content, toggle))
                    {
                        Logger.Warn($"[配置同步] 还原描述应用失败（{rel}），该文件按无还原描述处理：{toggle.Path}");
                        return false;
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllText(dest, content);
                Logger.Info($"[配置同步] 插队文件已还原启停并镜像：{rel}");
                return true;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(source, dest, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[配置同步] 镜像文件失败（{rel}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>应用单个还原描述 toggle：array 型按 keyField 匹配 initial 设 enabledField（未覆盖元素不动）；map 型逐键设布尔（未覆盖键不动）。</summary>
    internal static bool ApplyToggle(ref string content, ToggleRestore toggle)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(content);
            if (root is null)
            {
                return false;
            }
            if (toggle.Type == "array")
            {
                JsonNode? node = LocateNode(root, toggle.Path);
                if (node is not JsonArray array)
                {
                    return false;
                }
                foreach (JsonNode? element in array)
                {
                    if (element is null)
                    {
                        continue;
                    }
                    string? key = element[toggle.KeyField]?.ToString();
                    if (string.IsNullOrWhiteSpace(key) || !toggle.Initial.TryGetValue(key, out bool value))
                    {
                        continue;
                    }
                    element[toggle.EnabledField] = value;
                }
            }
            else if (toggle.Type == "map")
            {
                JsonNode? node = LocateNode(root, toggle.Path);
                if (node is not JsonObject obj)
                {
                    return false;
                }
                foreach (KeyValuePair<string, bool> kv in toggle.Initial)
                {
                    obj[kv.Key] = kv.Value;
                }
            }
            else
            {
                return false;
            }
            content = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>还原描述路径 DSL 定位：支持标识符[下标]或标识符[key=value]选择数组元素（如 instances[id=main].tasks）。</summary>
    internal static JsonNode? LocateNode(JsonNode root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        JsonNode? node = root;
        foreach (string segment in path.Split('.'))
        {
            string name = segment;
            int index = -1;
            string? selectorKey = null;
            string? selectorValue = null;
            int bracket = segment.IndexOf('[');
            if (bracket >= 0 && segment.Length > bracket + 1 && segment[^1] == ']')
            {
                name = segment.Substring(0, bracket);
                string selector = segment.Substring(bracket + 1, segment.Length - bracket - 2);
                if (!int.TryParse(selector, out index))
                {
                    index = -1;
                    int equal = selector.IndexOf('=');
                    if (equal <= 0 || equal >= selector.Length - 1)
                    {
                        return null;
                    }
                    selectorKey = selector[..equal];
                    selectorValue = selector[(equal + 1)..];
                }
            }
            if (node is JsonObject obj)
            {
                if (string.IsNullOrEmpty(name))
                {
                    return null;
                }
                node = obj[name];
                if (selectorKey is not null)
                {
                    if (node is not JsonArray selectedArray)
                    {
                        return null;
                    }
                    JsonNode? selectedNode = null;
                    foreach (JsonNode? item in selectedArray)
                    {
                        if (item is not JsonObject selected)
                        {
                            continue;
                        }
                        string? actual = selected[selectorKey!]?.ToString()?.Trim('"');
                        if (string.Equals(actual, selectorValue, StringComparison.Ordinal))
                        {
                            selectedNode = selected;
                            break;
                        }
                    }
                    node = selectedNode;
                }
            }
            else if (node is JsonArray arr)
            {
                if (index < 0 || index >= arr.Count)
                {
                    return null;
                }
                node = arr[index];
                index = -1;
            }
            else
            {
                return null;
            }
            if (node is null)
            {
                return null;
            }
            if (index >= 0)
            {
                if (node is not JsonArray arr2 || index >= arr2.Count)
                {
                    return null;
                }
                node = arr2[index];
            }
        }
        return node;
    }

    /* ---------------- 会话与恢复 ---------------- */

    /// <summary>操作前自愈：若存在未完成的交换标记且缓存区有内容，先完成还原（安全优先：原配置必还原）。失败交由后台重试。</summary>
    public static void RecoverIfNeeded(string scriptId, string userName, string configPath)
    {
        ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
        if (mark is null)
        {
            return;
        }
        string cache = ConfigSwapPaths.CacheDir(scriptId, userName);
        if (!Directory.Exists(cache) || !Directory.EnumerateFileSystemEntries(cache).Any())
        {
            // v0.6.9+（P2）：语义对齐 TryRecoverItem——GeneratedTemplate（编辑会话模板产物）仍需 DoRestore
            // 清理（恢复编辑前状态）；非模板会话 cache 空 = 现场已还原，仅清标记（避免窄窗口误删用户新写入的 config）。
            if (mark.GeneratedTemplate)
            {
                DoRestore(scriptId, userName, mark);
            }
            else
            {
                ConfigSessionMark.Clear(scriptId, userName);
            }
            ConfigSwapPrimitives.TryDeleteDir(ConfigSwapPaths.RetryStoreDir(scriptId, userName));
            return;
        }
        Logger.Info($"[恢复] 检测到脚本「{scriptId}」用户「{userName}」存在未完成的配置交换，正在还原。");
        try
        {
            DoRestore(scriptId, userName, mark);
            Audit.Log(Audit.System, "恢复配置交换", $"{mark.ConfigPath}（用户 {userName}）");
        }
        catch (Exception ex)
        {
            Audit.Log(Audit.System, "恢复配置交换失败", $"{mark.ConfigPath}（用户 {userName}）：{ex.Message}");
            EnqueuePendingRecover(scriptId, userName);
        }
    }

    /// <summary>启动恢复：扫描全部残留标记并还原（幂等；original 为空则仅清标记，不动现场）；同时恢复未还原的配置替换。
    /// 还原失败（如脚本孤儿进程仍占用配置目录）记入待办，由 <see cref="StartRecoveryRetry"/> 后台循环延迟重试。</summary>
    public static void RecoverInterrupted()
    {
        try
        {
            ConfigSwapPaths.MigrateLegacyLayout();
            RecoverStoreTransactions();
            if (!Directory.Exists(AppPaths.DataDir))
            {
                return;
            }
            foreach (string scriptDir in Directory.GetDirectories(AppPaths.DataDir))
            {
                string scriptId = Path.GetFileName(scriptDir);
                TryRecoverItem(scriptId, null);
                foreach (string userDir in Directory.GetDirectories(scriptDir))
                {
                    string userName = Path.GetFileName(userDir);
                    TryRecoverItem(scriptId, userName);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 扫描未完成配置交换失败：{ex.Message}");
        }
    }

    /// <summary>恢复自动更新配置的目录事务：store 缺失时提升 store.previous，临时目录只作为未完成事务清理。</summary>
    private static void RecoverStoreTransactions()
    {
        if (!Directory.Exists(AppPaths.DataDir))
        {
            return;
        }
        foreach (string temp in Directory.GetDirectories(AppPaths.DataDir, "store.tmp", SearchOption.AllDirectories))
        {
            string store = temp[..^4];
            string previous = store + ".previous";
            try
            {
                if (!Directory.Exists(store) && Directory.Exists(previous))
                {
                    Directory.Move(previous, store);
                    Logger.Warn($"[恢复] 自动更新配置事务中断，已恢复旧用户快照：{store}");
                }
                ConfigSwapPrimitives.TryDeleteDir(temp);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 清理自动更新配置临时事务失败（{temp}）：{ex.Message}");
            }
        }
    }

    /* ---------------- 延迟恢复重试（崩溃后脚本孤儿进程退出后自动还原） ---------------- */

    private static readonly List<(string ScriptId, string? UserName)> PendingRecovers = new();

    private static readonly object PendingSync = new();

    private static CancellationTokenSource? _retryCts;

    /// <summary>尝试恢复一个脚本/用户的全部残留（配置替换 + 配置交换）；返回是否已完全恢复，失败记入待办。</summary>
    private static bool TryRecoverItem(string scriptId, string? userName)
    {
        // v0.6.6+：脚本进程仍在运行（如「强制关闭服务 + 先启动脚本再启动服务」场景）时跳过全部恢复动作，
        // 避免误删/误覆盖正在使用的配置；记入待办，进程退出后由后台重试循环自动完成恢复。
        if (ScriptProcessRunning(scriptId))
        {
            Logger.Info($"[恢复] 脚本 {scriptId} 进程仍在运行，等待其退出后恢复配置。");
            EnqueuePendingRecover(scriptId, userName);
            return false;
        }
        bool ok = true;
        if (HasBackupResidue(scriptId, userName) && !RecoverBackupQuiet(scriptId, userName))
        {
            ok = false;
        }
        if (ok && !string.IsNullOrWhiteSpace(userName))
        {
            ConfigSessionMark? mark = ConfigSessionMark.TryRead(scriptId, userName);
            if (mark is null && File.Exists(ConfigSessionMark.MarkFile(scriptId, userName)))
            {
                ok = RecoverCorruptMark(scriptId, userName);
            }
            mark = ok ? ConfigSessionMark.TryRead(scriptId, userName) : null;
            if (mark is not null)
            {
                RestoreHiddenQuiet(scriptId, userName, mark.ConfigPath);
                string cache = ConfigSwapPaths.CacheDir(scriptId, userName);
                if (!Directory.Exists(cache) || !Directory.EnumerateFileSystemEntries(cache).Any())
                {
                    // v0.6.9+（P2）：与 RecoverIfNeeded 语义对齐——GeneratedTemplate（编辑会话模板产物）仍需
                    // DoRestore 清理（恢复编辑前状态，如重启后编辑会话恢复用例）；非模板会话 cache 空 =
                    // 现场已还原，仅清标记（此前一律 DoRestore，对 Missing 再执行会按「会话产物」删除
                    // config 位置当前文件，含崩溃后用户新写入的配置——窄窗口误删）。
                    if (mark.GeneratedTemplate)
                    {
                        DoRestore(scriptId, userName, mark);
                    }
                    else
                    {
                        ConfigSessionMark.Clear(scriptId, userName);
                    }
                }
                else if (!RecoverSwapQuiet(scriptId, userName, mark))
                {
                    ok = false;
                }
            }
        }
        if (!ok)
        {
            EnqueuePendingRecover(scriptId, userName);
        }
        return ok;
    }

    /// <summary>会话标记损坏时，使用当前脚本固化的 ConfigPath 和 original 目录形态做保守恢复。</summary>
    private static bool RecoverCorruptMark(string scriptId, string userName)
    {
        ScriptInstance? script = RuntimeContext.Instance.FindScript(scriptId);
        if (script is null || string.IsNullOrWhiteSpace(script.ConfigPath))
        {
            Logger.Error($"[错误] 配置会话标记损坏且无法找到脚本配置路径：脚本 {scriptId} / 用户 {userName}");
            EnqueuePendingRecover(scriptId, userName);
            return false;
        }
        string cache = ConfigSwapPaths.CacheDir(scriptId, userName);
        var mark = new ConfigSessionMark
        {
            ScriptId = scriptId,
            UserName = userName,
            ConfigPath = script.ConfigPath,
            OriginalKind = string.IsNullOrWhiteSpace(Path.GetExtension(script.ConfigPath)) ? "dir" : "file",
            Phase = "run",
        };
        try
        {
            if (Directory.Exists(cache) && Directory.EnumerateFileSystemEntries(cache).Any())
            {
                DoRestore(scriptId, userName, mark);
            }
            else
            {
                ConfigSessionMark.Clear(scriptId, userName);
            }
            Logger.Warn($"[恢复] 配置会话标记损坏，已按当前脚本配置路径完成保守恢复：脚本 {scriptId} / 用户 {userName}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[错误] 配置会话标记损坏，保守恢复失败（脚本 {scriptId} / 用户 {userName}）：{ex.Message}");
            EnqueuePendingRecover(scriptId, userName);
            return false;
        }
    }

    /// <summary>解析脚本运行时启动目标（含 Args 显式路径语义）并检测进程是否在运行；脚本已删除时返回 false（保持旧恢复行为）。</summary>
    private static bool ScriptProcessRunning(string scriptId)
    {
        ScriptInstance? script = RuntimeContext.Instance.FindScript(scriptId);
        if (script is null || string.IsNullOrWhiteSpace(script.MainExe))
        {
            return false;
        }
        string workingDir = string.IsNullOrWhiteSpace(script.RootPath)
            ? Path.GetDirectoryName(script.MainExe) ?? ""
            : script.RootPath;
        string launchExe = SystemActions.ResolveLaunchTarget(script.MainExe, workingDir, script.Args).ExePath;
        return SystemActions.IsExeRunning(launchExe);
    }

    private static bool HasBackupResidue(string scriptId, string? userName)
    {
        string dir = ConfigSwapPaths.ReplaceBackupDir(scriptId, userName);
        return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
    }

    private static bool RecoverBackupQuiet(string scriptId, string? userName)
    {
        Logger.Info($"[恢复] 检测到未还原的配置替换，还原脚本 {scriptId} 用户 {userName ?? "(无用户)"} 的配置。");
        try
        {
            bool restored = RestoreConfigReplacements(scriptId, userName);
            Audit.Log(Audit.System, "启动恢复配置替换", $"脚本 {scriptId} / 用户 {userName ?? "(无用户)"}");
            return restored;
        }
        catch (Exception ex)
        {
            Audit.Log(Audit.System, "启动恢复配置替换失败", $"脚本 {scriptId}：{ex.Message}");
            return false;
        }
    }

    /// <summary>恢复编辑会话隐藏的配置（幂等）：编辑会话崩溃/重启后，把暂存在 edit-hidden 的配置移回 config 目录并清理目录。</summary>
    private static void RestoreHiddenQuiet(string scriptId, string userName, string configPath)
    {
        string hideDir = ConfigSwapPaths.HiddenConfigDir(scriptId, userName);
        if (!Directory.Exists(hideDir) || !Directory.EnumerateFileSystemEntries(hideDir).Any())
        {
            return;
        }
        string? dir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[恢复] 重建配置目录失败（{dir}）：{ex.Message}");
                return;
            }
        }
        foreach (string file in Directory.GetFiles(hideDir))
        {
            try
            {
                File.Move(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[恢复] 恢复隐藏配置失败（保持原样）：{file}（{ex.Message}）");
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

    private static bool RecoverSwapQuiet(string scriptId, string? userName, ConfigSessionMark mark)
    {        Logger.Info($"[恢复] 上次会话中断，还原脚本 {scriptId} 用户 {userName} 的配置。");
        try
        {
            DoRestore(scriptId, userName!, mark);
            ConfigSwapPrimitives.TryDeleteDir(ConfigSwapPaths.RetryStoreDir(scriptId, userName!));
            Audit.Log(Audit.System, "启动恢复配置交换", $"脚本 {scriptId} / 用户 {userName}（{mark.ConfigPath}）");
            return true;
        }
        catch (Exception ex)
        {
            Audit.Log(Audit.System, "启动恢复配置交换失败", $"脚本 {scriptId} / 用户 {userName}：{ex.Message}");
            return false;
        }
    }

    private static void EnqueuePendingRecover(string scriptId, string? userName)
    {
        lock (PendingSync)
        {
            if (!PendingRecovers.Any(item => item.ScriptId == scriptId && item.UserName == userName))
            {
                PendingRecovers.Add((scriptId, userName));
            }
        }
    }

    /// <summary>启动后台恢复重试循环：每 10 秒尝试还原待办项（孤儿进程退出/文件解锁后自动完成），直至全部成功或进程退出。</summary>
    public static void StartRecoveryRetry()
    {
        if (_retryCts is not null)
        {
            return;
        }
        var cts = new CancellationTokenSource();
        _retryCts = cts;
        _ = Task.Run(() => RecoveryRetryLoopAsync(cts.Token));
        Logger.Info("配置恢复重试循环已启动。");
    }

    public static void StopRecoveryRetry()
    {
        try
        {
            _retryCts?.Cancel();
        }
        catch
        {
        }
        _retryCts = null;
    }

    private static async Task RecoveryRetryLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                List<(string ScriptId, string? UserName)> pending;
                lock (PendingSync)
                {
                    pending = new List<(string, string?)>(PendingRecovers);
                }
                foreach ((string scriptId, string? userName) in pending)
                {
                    try
                    {
                        if (TryRecoverItem(scriptId, userName))
                        {
                            lock (PendingSync)
                            {
                                PendingRecovers.RemoveAll(item => item.ScriptId == scriptId && item.UserName == userName);
                            }
                            Logger.Info($"[恢复] 延迟重试成功：脚本 {scriptId} / 用户 {userName ?? "(无用户)"} 的配置已还原。");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[恢复] 延迟重试异常（脚本 {scriptId}）：{ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 配置恢复重试循环异常：{ex.Message}");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>执行还原：清 config（当前形态），original → config 还原原配置，随后清除标记。
    /// original 为空（首次会话）时：清理会话期间在 config 位置产生的文件/目录，还原为编辑前状态——
    /// ① 编辑会话生成的配置模板（GeneratedTemplate）；② 运行会话原配置形态为 Missing（运行前 config 位置不存在，
    /// 运行生效的 store 快照为会话产物，必须删除，否则残留污染 config 位置与后续快照）。</summary>
    public static void DoRestore(string scriptId, string userName, ConfigSessionMark mark)
    {
        string cache = ConfigSwapPaths.CacheDir(scriptId, userName);
        if (!Directory.Exists(cache) || !Directory.EnumerateFileSystemEntries(cache).Any())
        {
            bool restoreMissing = mark.GeneratedTemplate
                || PathKindUtil.Parse(mark.OriginalKind) == PathKind.Missing;
            if (restoreMissing)
            {
                PathKind current = PathKindUtil.KindOf(mark.ConfigPath);
                if (current != PathKind.Missing)
                {
                    // 模板目录形态（v0.6.3+）：先按清单删除复制生成的模板文件，再对 configPath 位置兜底清理（防残留）
                    DeleteTemplateFiles(mark);
                    // 删除失败自然抛出（ClearPath 带重试），标记保留，交由调用方（自愈/后台延迟重试）再次尝试
                    ConfigSwapPrimitives.ClearPath(mark.ConfigPath, current);
                    Logger.Info($"[恢复] 已清理会话期间生成的配置（还原为不存在）：{mark.ConfigPath}");
                }
            }
            ConfigSessionMark.Clear(scriptId, userName);
            return;
        }
        // v0.7.5（台账外）：cache 非空路径同样先按清单删除模板兄弟文件——StartVisible 失败/CancelEdit 且原配置存在时，
        // 文件型 config 模板复制到父目录的非 ConfigPath 同名文件（如 maa_option.json）此前残留。
        DeleteTemplateFiles(mark);
        PathKind currentState = PathKindUtil.KindOf(mark.ConfigPath);
        ConfigSwapPrimitives.ClearPath(mark.ConfigPath, currentState);
        ConfigSwapPrimitives.MoveAs(cache, mark.ConfigPath, ConfigSwapPrimitives.RestoreKind(mark));
        ConfigSessionMark.Clear(scriptId, userName);
    }

    /// <summary>按 TemplateFiles 清单删除编辑会话生成的模板文件（相对 ConfigPath 父目录）；删除失败保留标记交自愈重试。</summary>
    private static void DeleteTemplateFiles(ConfigSessionMark mark)
    {
        if (mark.TemplateFiles.Count == 0)
        {
            return;
        }
        string? baseDir = Path.GetDirectoryName(mark.ConfigPath);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            return;
        }
        foreach (string rel in mark.TemplateFiles)
        {
            try
            {
                string dest = Path.Combine(baseDir, rel);
                if (File.Exists(dest))
                {
                    File.Delete(dest);
                }
            }
            catch
            {
                // 删除失败保留标记，交由调用方（自愈/后台延迟重试）再次尝试
            }
        }
    }
}
