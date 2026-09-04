using System.Text.Json;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

/// <summary>写入配置交换标记的运行时快照；启动恢复阶段不依赖插件重新解析。</summary>
internal sealed record ConfigSessionRuntimeMetadata(
    string WorkingDirectory,
    string LaunchExe,
    string ProcessIdentity,
    string ProfileHash,
    string PluginName,
    string PluginVersion,
    string ConfigKind);

/// <summary>配置交换会话标记：交换开始写入、完成删除；崩溃后可据此恢复（安全优先：原配置必还原）。</summary>
internal sealed class ConfigSessionMark
{
    public string ScriptId { get; set; } = "";

    public string UserId { get; set; } = "";

    public string ConfigPath { get; set; } = "";

    public string SessionPhase { get; set; } = "";

    public string ConfigKind { get; set; } = "";

    public string WorkingDirectory { get; set; } = "";

    public string LaunchExe { get; set; } = "";

    public string ProcessIdentity { get; set; } = "";

    public string ProfileHash { get; set; } = "";

    public string PluginName { get; set; } = "";

    public string PluginVersion { get; set; } = "";

    /// <summary>编辑会话模式：normal（快照交换，默认）/ fresh（全新配置，原配置移入缓存区）/ reuse（复用现场配置，无文件动作）。</summary>
    public string EditMode { get; set; } = "normal";

    /// <summary>全新配置编辑会话且原配置形态为 Missing：缓存区为空时 config 位置的脚本生成物仍需还原清理。</summary>
    public bool NeedsFreshRestore =>
        string.Equals(EditMode, "fresh", StringComparison.OrdinalIgnoreCase)
        && PathKindUtil.Parse(ConfigKind) == PathKind.Missing;

    public DateTime StartedAt { get; set; } = DateTime.Now;

    private static readonly JsonSerializerOptions Options = new()
    {
        // 会话标记属于当前磁盘协议；旧版本字段和 camelCase 现场不再参与恢复。
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
    };

    private static readonly string[] RequiredProperties =
    [
        nameof(ScriptId),
        nameof(UserId),
        nameof(ConfigPath),
        nameof(SessionPhase),
        nameof(ConfigKind),
        nameof(WorkingDirectory),
        nameof(LaunchExe),
        nameof(ProcessIdentity),
        nameof(ProfileHash),
        nameof(PluginName),
        nameof(PluginVersion),
        nameof(EditMode),
        nameof(StartedAt),
    ];

    private static readonly HashSet<string> AllowedProperties =
        RequiredProperties.Append(nameof(NeedsFreshRestore)).ToHashSet(StringComparer.Ordinal);

    public static string MarkFile(string scriptId, string userId)
    {
        return Path.Combine(AppPaths.DataDir, scriptId, userId, ".session");
    }

    public static string BackupMarkFile(string scriptId, string userId)
    {
        return MarkFile(scriptId, userId) + ".bak";
    }

    internal static ConfigSessionRuntimeMetadata FromScript(
        ScriptInstance script,
        string profileHash = "",
        string pluginVersion = "")
    {
        string workingDirectory = string.IsNullOrWhiteSpace(script.RootPath)
            ? Path.GetDirectoryName(script.MainExe) ?? ""
            : script.RootPath;
        string launchExe = string.IsNullOrWhiteSpace(script.MainExe)
            ? ""
            : SystemActions.ResolveLaunchTarget(script.MainExe, workingDirectory, script.Args).ExePath;
        return new ConfigSessionRuntimeMetadata(
            workingDirectory,
            launchExe,
            string.IsNullOrWhiteSpace(launchExe) ? "" : Path.GetFileNameWithoutExtension(launchExe),
            profileHash,
            script.PluginType,
            pluginVersion,
            PathKindUtil.Text(PathKindUtil.KindOf(script.ConfigPath)));
    }

    public void Write()
    {
        ValidateCurrent();
        Directory.CreateDirectory(Path.GetDirectoryName(MarkFile(ScriptId, UserId))!);
        string json = JsonSerializer.Serialize(this, Options);
        // 先写冗余现场，再替换主标记；任一写入中断都至少保留一份可解析元数据。
        JsonUtil.WriteAtomic(BackupMarkFile(ScriptId, UserId), json);
        JsonUtil.WriteAtomic(MarkFile(ScriptId, UserId), json);
    }

    public static ConfigSessionMark? TryRead(string scriptId, string userId)
    {
        string primary = MarkFile(scriptId, userId);
        string backup = BackupMarkFile(scriptId, userId);
        if (!File.Exists(primary) && !File.Exists(backup))
        {
            return null;
        }

        foreach (string file in new[] { primary, backup })
        {
            if (!File.Exists(file))
            {
                continue;
            }
            try
            {
                string json = File.ReadAllText(file);
                using JsonDocument document = JsonDocument.Parse(json);
                if (!IsCurrentDocument(document))
                {
                    Logger.Warn($"[警告] 配置会话标记不是当前格式，保留现场：{file}");
                    continue;
                }
                ConfigSessionMark? mark = JsonSerializer.Deserialize<ConfigSessionMark>(json, Options);
                if (mark is null || !mark.IsValidCurrent())
                {
                    Logger.Warn($"[警告] 配置会话标记字段无效，保留现场：{file}");
                    continue;
                }
                return mark;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 读取配置会话标记失败（{file}）：{ex.Message}");
            }
        }
        return null;
    }

    private bool IsValidCurrent() =>
        !string.IsNullOrWhiteSpace(ScriptId)
        && !string.IsNullOrWhiteSpace(UserId)
        && !string.IsNullOrWhiteSpace(ConfigPath)
        && SessionPhase is "run" or "edit"
        && (ConfigKind is "missing" or "file" or "dir")
        && (EditMode is "normal" or "fresh" or "reuse");

    private void ValidateCurrent()
    {
        if (!IsValidCurrent())
        {
            throw new InvalidDataException("配置会话标记字段无效");
        }
    }

    private static bool IsCurrentDocument(JsonDocument document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var properties = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (RequiredProperties.Any(property => !properties.Contains(property)))
        {
            return false;
        }
        // 只接受当前协议的完整字段集合；任何旧别名或未知字段都保留现场并交由人工处理。
        return properties.All(AllowedProperties.Contains);
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
        try
        {
            File.Delete(BackupMarkFile(scriptId, userName));
        }
        catch
        {
        }
    }
}
