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

/// <summary>
/// 配置交换会话/恢复层（从 UserConfigManager 拆出）：配置替换（replaceConfigs）、
/// .session 标记、操作前自愈 + 启动扫描恢复 + 后台延迟重试。文件原语见 <see cref="ConfigSwapPrimitives"/>，
/// 数据目录定位见 <see cref="ConfigPaths"/>。
/// </summary>
internal static partial class ConfigSwapSession
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
        return ConfigPathSafety.ResolveWithin(configPath, rel);
    }

    /// <summary>应用配置替换：把 script 目录内文件复制覆盖到 config 对应位置；首次替换前备份原始内容到 swap-backup（含 .meta 记录 configPath 与新增文件清单）。</summary>
    public static string? ApplyConfigReplacements(string scriptId, string? userName, string configPath, List<string> replacements)
    {
        string scriptDir = ConfigPaths.ScriptDir(scriptId, userName);
        string backupDir = ConfigPaths.ReplaceBackupDir(scriptId, userName);
        string metaPath = Path.Combine(backupDir, ".meta");
        List<string> newFiles = ReadMetaNewFiles(metaPath);
        foreach (string rel in replacements)
        {
            string? source = ConfigPathSafety.ResolveWithin(scriptDir, rel);
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
        string backupDir = ConfigPaths.ReplaceBackupDir(scriptId, userName);
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

}
