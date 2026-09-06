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
    string ConfigKind)
{
    /// <summary>已解析并冻结的附加配置路径；启动恢复不重新加载插件 profile。</summary>
    public IReadOnlyList<ConfigSessionExtraPath> ExtraConfigPaths { get; init; } = Array.Empty<ConfigSessionExtraPath>();
}

/// <summary>会话标记中的附加配置恢复描述；OriginalKind 在任何现场修改前捕获。</summary>
internal sealed class ConfigSessionExtraPath
{
    public string Path { get; set; } = "";

    public string OriginalKind { get; set; } = "missing";

    public ConfigSessionExtraPath Clone() => new()
    {
        Path = Path,
        OriginalKind = OriginalKind,
    };
}

/// <summary>编辑事务隔离项目的原始路径与原始形态。</summary>
internal sealed class ConfigSessionIsolationPath
{
    public string Path { get; set; } = "";

    public string OriginalKind { get; set; } = "missing";

    public ConfigSessionIsolationPath Clone() => new()
    {
        Path = Path,
        OriginalKind = OriginalKind,
    };
}

/// <summary>fresh 编辑完成后待写入用户绑定的输入值。</summary>
internal sealed class ConfigEditPendingInput
{
    public string Name { get; set; } = "";

    public string Value { get; set; } = "";

    public ConfigEditPendingInput Clone() => new() { Name = Name, Value = Value };
}

/// <summary>编辑准备阶段的隔离候选与待提交输入。</summary>
internal sealed record ConfigEditPreparationOptions(
    bool IsolateSiblingCandidates,
    IReadOnlyList<string> CandidatePaths,
    ConfigEditPendingInput? PendingConfigInput = null);

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

    /// <summary>本次会话已冻结的 extraConfigPaths。</summary>
    public List<ConfigSessionExtraPath> ExtraConfigPaths { get; set; } = new();

    /// <summary>编辑会话模式：normal（快照交换，默认）/ fresh（全新配置，原配置移入缓存区）/ reuse（复用现场配置）。</summary>
    public string EditMode { get; set; } = "normal";

    /// <summary>编辑期间隔离的兄弟配置原始路径清单。</summary>
    public List<ConfigSessionIsolationPath> EditIsolationPaths { get; set; } = new();

    /// <summary>主快照提交后等待写入用户绑定的输入值。</summary>
    public ConfigEditPendingInput? PendingConfigInput { get; set; }

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
        nameof(ExtraConfigPaths),
        nameof(EditMode),
        nameof(StartedAt),
    ];

    private static readonly HashSet<string> AllowedProperties =
        RequiredProperties
            .Append(nameof(NeedsFreshRestore))
            .Append(nameof(EditIsolationPaths))
            .Append(nameof(PendingConfigInput))
            .ToHashSet(StringComparer.Ordinal);

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
        string pluginVersion = "",
        IReadOnlyList<string>? extraConfigPaths = null)
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
            PathKindUtil.Text(PathKindUtil.KindOf(script.ConfigPath)))
        {
            ExtraConfigPaths = FromExtraPaths(extraConfigPaths),
        };
    }

    internal static List<ConfigSessionExtraPath> FromExtraPaths(IReadOnlyList<string>? paths)
    {
        var result = new List<ConfigSessionExtraPath>();
        if (paths is null)
        {
            return result;
        }
        foreach (string rawPath in paths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }
            string path = Path.GetFullPath(rawPath.Trim());
            if (result.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            result.Add(new ConfigSessionExtraPath
            {
                Path = path,
                OriginalKind = PathKindUtil.Text(PathKindUtil.KindOf(path)),
            });
        }
        return result;
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
        && SessionPhase is "run" or "edit" or "edit-commit-pending"
        && (ConfigKind is "missing" or "file" or "dir")
        && (EditMode is "normal" or "fresh" or "reuse")
        && ExtraConfigPaths is not null
        && ExtraConfigPaths.All(IsValidExtraPath)
        && EditIsolationPaths is not null
        && EditIsolationPaths.All(IsValidIsolationPath)
        && (PendingConfigInput is null || IsValidPendingInput(PendingConfigInput));

    private static bool IsValidExtraPath(ConfigSessionExtraPath entry)
    {
        return entry is not null
            && !string.IsNullOrWhiteSpace(entry.Path)
            && Path.IsPathRooted(entry.Path)
            && entry.OriginalKind is "missing" or "file" or "dir";
    }

    private static bool IsValidIsolationPath(ConfigSessionIsolationPath entry)
    {
        return entry is not null
            && !string.IsNullOrWhiteSpace(entry.Path)
            && Path.IsPathRooted(entry.Path)
            && entry.OriginalKind is "missing" or "file" or "dir";
    }

    private static bool IsValidPendingInput(ConfigEditPendingInput input)
    {
        return !string.IsNullOrWhiteSpace(input.Name)
            && !string.IsNullOrWhiteSpace(input.Value)
            && input.Name.Length <= 128
            && input.Value.Length <= 512
            && char.IsLetter(input.Name[0])
            && input.Name.All(character => char.IsLetterOrDigit(character) || character == '_');
    }

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
