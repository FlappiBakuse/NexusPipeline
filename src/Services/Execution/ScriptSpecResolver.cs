using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Extensibility;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Plugins;

namespace NexusPipeline.Services.Execution;

/// <summary>本次运行使用的判断脚本来源描述。</summary>
internal sealed record ResolvedJudgeScript(
    bool Enabled,
    string Language,
    string SourceKind,
    string SourcePath,
    string ContentHash);

/// <summary>
/// 脚本声明解析后的不可变运行时快照。
/// Script 是兼容现有执行域的完整快照；PluginVersion/ProfileHash 用于恢复、调度诊断与后续存储元数据。
/// </summary>
internal sealed record ResolvedScriptSpec(
    ScriptInstance Script,
    string PluginVersion,
    ResolvedJudgeScript JudgeScript,
    string ProfileHash,
    string? Error = null,
    ConfigValidatorDescriptor? ConfigValidator = null)
{
    public bool Succeeded => string.IsNullOrWhiteSpace(Error);
}

/// <summary>
/// 把持久化脚本声明解析为一次运行/编辑所需的有效配置。
/// 专项脚本每次解析读取当前插件 profile，通用判断脚本从用户资产目录读取。
/// </summary>
internal sealed class ScriptSpecResolver
{
    private readonly IPluginCapabilityResolver _capabilities;
    private readonly IPluginAvailability _availability;
    private readonly JudgeScriptStore _judgeScripts;
    private readonly PluginManager? _plugins;

    public ScriptSpecResolver(
        IPluginCapabilityResolver capabilities,
        IPluginAvailability availability,
        JudgeScriptStore? judgeScripts = null,
        PluginManager? plugins = null)
    {
        _capabilities = capabilities;
        _availability = availability;
        _judgeScripts = judgeScripts ?? new JudgeScriptStore(AppPaths.JudgeScriptsDir);
        _plugins = plugins;
    }

    public ResolvedScriptSpec Resolve(ScriptInstance declaration)
    {
        ScriptInstance script = declaration.Clone();
        if (string.IsNullOrWhiteSpace(script.PluginType))
        {
            return ResolveGeneric(script);
        }

        string? unavailable = PluginAvailability.GetUnavailableReason(script, _availability);
        if (unavailable is not null)
        {
            return Failed(script, unavailable, "");
        }

        ScriptProfile? profile = _capabilities.ResolveProfile(script.PluginType.Trim(), script.RootPath.Trim(), script.PluginInputs);
        if (profile is null)
        {
            return Failed(
                script,
                $"专项插件「{script.PluginType}」无法从脚本根目录推导当前配置，请检查根目录及插件文件",
                "");
        }

        script.MainExe = profile.MainExe;
        script.Args = profile.Args;
        script.ConfigPath = profile.ConfigPath;
        script.LogPath = profile.LogPath;
        script.SuccessKeywords = "";
        script.FailureKeywords = "";
        script.AutoUpdateConfig = true;
        script.JudgeScriptEnabled = !string.IsNullOrWhiteSpace(profile.JudgeScript);
        script.JudgeScriptLanguage = JudgeScriptStore.NormalizeLanguage(profile.JudgeScriptLanguage);
        script.JudgeScript = profile.JudgeScript ?? "";

        ResolvedJudgeScript judge = new(
            script.JudgeScriptEnabled,
            script.JudgeScriptLanguage,
            "plugin-file",
            profile.JudgeScriptPath ?? "",
            Hash(profile.JudgeScript ?? ""));
        return new ResolvedScriptSpec(
            script,
            profile.PluginVersion ?? "",
            judge,
            ComputeProfileHash(script, profile.PluginName ?? "", profile.PluginVersion ?? "", judge),
            ConfigValidator: ResolveConfigValidator(script.PluginType));
    }

    /// <summary>
    /// 解析即将写入仓储的候选脚本。通用脚本以候选对象中的源码为准，
    /// 这样更新或清空判断脚本时校验的都是本次提交内容。
    /// </summary>
    public ResolvedScriptSpec ResolveCandidate(ScriptInstance candidate)
    {
        ScriptInstance script = candidate.Clone();
        return string.IsNullOrWhiteSpace(script.PluginType)
            ? ResolveGeneric(script, preferInlineSource: true)
            : Resolve(script);
    }

    public ScriptInstance ResolveScript(ScriptInstance declaration)
    {
        return Resolve(declaration).Script;
    }

    private ResolvedScriptSpec ResolveGeneric(ScriptInstance script, bool preferInlineSource = false)
    {
        string language = JudgeScriptStore.NormalizeLanguage(script.JudgeScriptLanguage);
        string? source = preferInlineSource
            ? (string.IsNullOrWhiteSpace(script.JudgeScript) ? null : script.JudgeScript)
            : _judgeScripts.Load(script.Id, language);
        if (source is not null)
        {
            script.JudgeScript = source;
            script.JudgeScriptLanguage = language;
        }
        else if (!string.IsNullOrWhiteSpace(script.JudgeScript))
        {
            // 兼容未经过仓储的 API/测试对象；正式加载路径优先使用独立资产文件。
            script.JudgeScriptLanguage = language;
        }
        else if (script.JudgeScriptEnabled)
        {
            return Failed(script, $"判断脚本资产不存在：{script.Id}{JudgeScriptStore.Extension(language)}", "");
        }

        bool enabled = script.JudgeScriptEnabled && !string.IsNullOrWhiteSpace(script.JudgeScript);
        string sourcePath = _judgeScripts.GetPath(script.Id, language);
        var judge = new ResolvedJudgeScript(
            enabled,
            language,
            "generic-file",
            sourcePath,
            Hash(script.JudgeScript));
        return new ResolvedScriptSpec(script, "", judge, ComputeProfileHash(script, "", "", judge));
    }

    private static ResolvedScriptSpec Failed(ScriptInstance script, string error, string version)
    {
        var judge = new ResolvedJudgeScript(
            script.JudgeScriptEnabled && !string.IsNullOrWhiteSpace(script.JudgeScript),
            JudgeScriptStore.NormalizeLanguage(script.JudgeScriptLanguage),
            string.IsNullOrWhiteSpace(script.PluginType) ? "generic-file" : "plugin-file",
            "",
            Hash(script.JudgeScript));
        return new ResolvedScriptSpec(script, version, judge, ComputeProfileHash(script, script.PluginType, version, judge), error);
    }

    private ConfigValidatorDescriptor? ResolveConfigValidator(string pluginType)
    {
        return _plugins is not null
            && _plugins.TryGetConfigValidator(pluginType, out ConfigValidatorDescriptor? descriptor)
            ? descriptor
            : null;
    }

    private static string ComputeProfileHash(
        ScriptInstance script,
        string pluginName,
        string pluginVersion,
        ResolvedJudgeScript judge)
    {
        var projection = new
        {
            script.Id,
            script.PluginType,
            script.RootPath,
            script.MainExe,
            script.Args,
            script.ConfigPath,
            script.LogPath,
            script.SuccessKeywords,
            script.FailureKeywords,
            script.JudgeScriptEnabled,
            script.JudgeScriptLanguage,
            JudgeContentHash = judge.ContentHash,
            PluginName = pluginName,
            PluginVersion = pluginVersion,
            script.AutoUpdateConfig,
        };
        return Hash(JsonSerializer.Serialize(projection));
    }

    private static string Hash(string? value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
