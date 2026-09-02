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

    public string UserName { get; set; } = "";

    /// <summary>用户 ID。UserName 保留为历史字段名；新标记同时写入，恢复时优先使用 UserId。</summary>
    public string UserId { get; set; } = "";

    public string ConfigPath { get; set; } = "";

    public string OriginalKind { get; set; } = "missing";

    public string Phase { get; set; } = "run";

    /// <summary>显式会话阶段，供恢复诊断与未来版本读取；与旧 Phase 字段保持双向兼容。</summary>
    public string SessionPhase { get; set; } = "";

    /// <summary>配置位置在会话开始时的形态；与 OriginalKind 同义，便于恢复元数据自描述。</summary>
    public string ConfigKind { get; set; } = "";

    public string WorkingDirectory { get; set; } = "";

    public string LaunchExe { get; set; } = "";

    public string ProcessIdentity { get; set; } = "";

    public string ProfileHash { get; set; } = "";

    public string PluginName { get; set; } = "";

    public string PluginVersion { get; set; } = "";

    /// <summary>编辑会话模式：normal（快照交换，默认）/ fresh（全新配置，原配置移入缓存区）/ reuse（复用现场配置，无文件动作）。
    /// 旧版本标记无此字段，反序列化后保持默认 normal，收尾与恢复行为与旧版一致。</summary>
    public string EditMode { get; set; } = "normal";

    /// <summary>全新配置编辑会话且原配置形态为 Missing：缓存区为空时 config 位置的脚本生成物仍需还原清理。
    /// （对应旧版 GeneratedTemplate 模板会话的恢复语义。）</summary>
    public bool NeedsFreshRestore =>
        string.Equals(EditMode, "fresh", StringComparison.OrdinalIgnoreCase)
        && PathKindUtil.Parse(OriginalKind) == PathKind.Missing;

    public DateTime StartedAt { get; set; } = DateTime.Now;

    private static readonly JsonSerializerOptions Options = new()
    {
        // 写盘改 PascalCase（与「磁盘 JSON = PascalCase」约定一致）；PropertyNameCaseInsensitive
        // 兼容读取旧版 camelCase 标记（旧版本崩溃现场仍可完整恢复，无需迁移）。
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string MarkFile(string scriptId, string userName)
    {
        return Path.Combine(AppPaths.DataDir, scriptId, userName, ".session");
    }

    public static string BackupMarkFile(string scriptId, string userName)
    {
        return MarkFile(scriptId, userName) + ".bak";
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
        UserId = string.IsNullOrWhiteSpace(UserId) ? UserName : UserId;
        SessionPhase = string.IsNullOrWhiteSpace(SessionPhase) ? Phase : SessionPhase;
        Phase = string.IsNullOrWhiteSpace(Phase) ? SessionPhase : Phase;
        ConfigKind = string.IsNullOrWhiteSpace(ConfigKind) ? OriginalKind : ConfigKind;
        OriginalKind = string.IsNullOrWhiteSpace(OriginalKind) ? ConfigKind : OriginalKind;
        Directory.CreateDirectory(Path.GetDirectoryName(MarkFile(ScriptId, UserName))!);
        string json = JsonSerializer.Serialize(this, Options);
        // 先写冗余现场，再替换主标记；任一写入中断都至少保留一份可解析元数据。
        JsonUtil.WriteAtomic(BackupMarkFile(ScriptId, UserName), json);
        JsonUtil.WriteAtomic(MarkFile(ScriptId, UserName), json);
    }

    public static ConfigSessionMark? TryRead(string scriptId, string userName)
    {
        string primary = MarkFile(scriptId, userName);
        string backup = BackupMarkFile(scriptId, userName);
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
                ConfigSessionMark? mark = JsonSerializer.Deserialize<ConfigSessionMark>(File.ReadAllText(file), Options);
                if (mark is null)
                {
                    continue;
                }
                mark.UserId = string.IsNullOrWhiteSpace(mark.UserId) ? mark.UserName : mark.UserId;
                mark.Phase = string.IsNullOrWhiteSpace(mark.Phase) ? mark.SessionPhase : mark.Phase;
                mark.SessionPhase = string.IsNullOrWhiteSpace(mark.SessionPhase) ? mark.Phase : mark.SessionPhase;
                mark.OriginalKind = string.IsNullOrWhiteSpace(mark.OriginalKind) ? mark.ConfigKind : mark.OriginalKind;
                mark.ConfigKind = string.IsNullOrWhiteSpace(mark.ConfigKind) ? mark.OriginalKind : mark.ConfigKind;
                return mark;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[警告] 读取配置会话标记失败（{file}）：{ex.Message}");
            }
        }
        return null;
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
