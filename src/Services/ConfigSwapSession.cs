using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Configuration;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>
/// 配置交换会话/恢复层（从 UserConfigManager 拆出）：配置替换（replaceConfigs）、
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
            // .meta 使用当前磁盘协议的 PascalCase 字段。
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
            JsonObject node = JsonNode.Parse(File.ReadAllText(metaPath)) as JsonObject
                ?? throw new InvalidDataException(".meta 根节点必须是对象");
            string configPath = node["ConfigPath"]?.ToString() ?? "";
            JsonArray files = node["NewFiles"] as JsonArray
                ?? throw new InvalidDataException(".meta 缺少当前格式 NewFiles");
            if (string.IsNullOrWhiteSpace(configPath))
            {
                throw new InvalidDataException(".meta 缺少当前格式 ConfigPath");
            }
            foreach (JsonNode? item in files)
            {
                string? text = item?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    list.Add(text);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[错误] .meta 替换清单不是当前格式（{metaPath}）：{ex.Message}");
            throw new InvalidDataException($"配置替换备份清单不是当前格式：{metaPath}", ex);
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
                JsonObject node = JsonNode.Parse(File.ReadAllText(metaPath)) as JsonObject
                    ?? throw new InvalidDataException(".meta 根节点必须是对象");
                configPath = node["ConfigPath"]?.ToString();
                JsonArray metaFiles = node["NewFiles"] as JsonArray
                    ?? throw new InvalidDataException(".meta 缺少当前格式 NewFiles");
                if (string.IsNullOrWhiteSpace(configPath))
                {
                    throw new InvalidDataException(".meta 缺少当前格式 ConfigPath");
                }
                foreach (JsonNode? item in metaFiles)
                {
                    string? text = item?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        newFiles.Add(text);
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
            Logger.Error($"[错误] 配置替换备份清单缺少当前格式 ConfigPath（{metaPath}）。");
            QuarantineInvalidBackup(backupDir, "缺少当前格式 ConfigPath");
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
                string store = ConfigSwapPaths.StoreDir(scriptId, userName);
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
                HashSet<string> swapFiles = ReadSwapFiles(ConfigSwapPaths.ReplaceBackupDir(scriptId, userName));
                ConfigRestoreDescriptor? descriptor = ReadRestoreDescriptor(ConfigSwapPaths.ScriptDir(scriptId, userName));
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
                if (File.Exists(ConfigSwapPaths.StoreTransactionBlockedPath(scriptId, userName)))
                {
                    throw new IOException($"配置快照事务已被阻断，请先处理现场：{ConfigSwapPaths.StoreTransactionBlockedPath(scriptId, userName)}");
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
    /// 契约见 docs/PLUGIN_API.md。</summary>
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

    internal static (string Content, Encoding Encoding) ReadTextPreservingEncoding(string path)
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

    internal static FileRestore? FindRestoreFile(ConfigRestoreDescriptor? descriptor, string rel)
    {
        return descriptor?.Files.FirstOrDefault(file =>
            string.Equals(NormalizeRelative(file.File), NormalizeRelative(rel), StringComparison.OrdinalIgnoreCase));
    }

    internal static string NormalizeRelative(string value)
    {
        return ConfigStoreDiff.NormalizeRelative(value);
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

    /* ---------------- 会话与恢复入口 ---------------- */

    private static ConfigSwapRecovery? _recovery;

    /// <summary>
    /// 装配配置交换恢复：组合根在进程初始化时注入脚本/用户快照数据源；
    /// 恢复路径不再反向依赖 RuntimeContext。未装配时调用恢复入口视为编程错误。
    /// </summary>
    public static void ConfigureRecovery(Func<string, ScriptInstance?> findScript, Func<IReadOnlyList<NexusUser>> snapshotUsers)
    {
        _recovery = new ConfigSwapRecovery(findScript, snapshotUsers);
    }

    private static ConfigSwapRecovery Recovery
    {
        get
        {
            if (_recovery is null)
            {
                throw new InvalidOperationException("配置交换恢复未装配（请先调用 ConfigSwapSession.ConfigureRecovery）。");
            }
            return _recovery;
        }
    }

    /// <summary>操作前自愈：恢复逻辑由 ConfigSwapRecovery 统一负责。</summary>
    public static void RecoverIfNeeded(string scriptId, string userName, string configPath)
    {
        Recovery.RecoverIfNeeded(scriptId, userName, configPath);
    }

    /// <summary>启动恢复扫描：恢复逻辑由 ConfigSwapRecovery 统一负责。</summary>
    public static void RecoverInterrupted(IReadOnlyList<NexusUser>? users = null)
    {
        Recovery.RecoverInterrupted(users);
    }

    public static void StartRecoveryRetry()
    {
        Recovery.StartRecoveryRetry();
    }

    public static void StopRecoveryRetry()
    {
        Recovery.StopRecoveryRetry();
    }

    public static void DoRestore(string scriptId, string userName, ConfigSessionMark mark)
    {
        Recovery.DoRestore(scriptId, userName, mark);
    }

}
