using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Plugins.DataSpecialized;
using NexusPipeline.Modules.Plugins.Runtime;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts.Persistence;
using NexusPipeline.Modules.Scripts.Validation;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users;
using NexusPipeline.Platform.Storage;
namespace NexusPipeline.Modules.Scripts.Resolution;


/// <summary>
/// 把持久化脚本声明解析为一次运行/编辑所需的有效配置。
/// 专项脚本每次解析读取当前插件 profile，通用判断脚本从用户资产目录读取。
/// </summary>
internal sealed class ScriptSpecResolver
{
    private readonly IPluginCapabilityResolver _capabilities;
    private readonly IPluginAvailability _availability;
    private readonly JudgeScriptStore _judgeScripts;
    private readonly IPluginExecutionProviderResolver? _executionProviders;

    public ScriptSpecResolver(
        IPluginCapabilityResolver capabilities,
        IPluginAvailability availability,
        JudgeScriptStore? judgeScripts = null,
        PluginManager? plugins = null,
        IPluginExecutionProviderResolver? executionProviders = null)
    {
        _capabilities = capabilities;
        _availability = availability;
        _judgeScripts = judgeScripts ?? new JudgeScriptStore(AppPaths.JudgeScriptsDir);
        _executionProviders = executionProviders ?? plugins;
    }

    /// <summary>
    /// 解析脚本声明。inputOverrides 为用户级输入值（UserScriptBinding.ConfigInputs），
    /// 优先于脚本实例持久化的 PluginInputs；通用脚本忽略该参数。
    /// </summary>
    public ResolvedScriptSpec Resolve(ScriptInstance declaration, IReadOnlyDictionary<string, string>? inputOverrides = null, string userId = "")
    {
        ScriptInstance script = declaration.Clone();
        if (!string.IsNullOrWhiteSpace(script.ExecutionProviderId))
            return ResolveProvider(script, userId);
        if (!string.IsNullOrWhiteSpace(script.ExecutionProviderConfigId))
            return Failed(script, "未声明执行 provider，不能使用 provider 配置身份", "");
        if (string.IsNullOrWhiteSpace(script.PluginType))
        {
            return ResolveGeneric(script);
        }

        string? unavailable = PluginAvailability.GetUnavailableReason(script, _availability);
        if (unavailable is not null)
        {
            return Failed(script, unavailable, "");
        }

        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (script.PluginInputs is not null)
        {
            foreach (KeyValuePair<string, string> item in script.PluginInputs)
            {
                inputs[item.Key] = item.Value;
            }
        }
        if (inputOverrides is not null)
        {
            foreach (KeyValuePair<string, string> item in inputOverrides)
            {
                inputs[item.Key] = item.Value;
            }
        }
        script.PluginInputs = inputs;

        ScriptProfile? profile = _capabilities.ResolveProfile(script.PluginType.Trim(), script.RootPath.Trim(), inputs);
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
            ComputeProfileHash(script, profile.PluginName ?? "", profile.PluginVersion ?? "", judge,
                profile.RootProcessRole.ToString(), profile.OutputEncoding),
            ConfigEditor: profile.ConfigEditor)
        {
            RootProcessRole = profile.RootProcessRole,
            OutputEncoding = profile.OutputEncoding,
            TaskProtocol = profile.TaskProtocol,
            ExtraConfigPaths = profile.ExtraConfigPaths,
            ConfigInputCandidates = profile.ConfigInputCandidates,
            ConfigInputName = profile.ConfigInputName,
            ConfigInputValue = profile.ConfigInputValue,
            ConfigInputCandidatePaths = profile.ConfigInputCandidatePaths,
            ConfigEdit = profile.ConfigEdit,
            SelfManagedPcLaunch = _capabilities.HasCapability(
                script.PluginType,
                PluginCapabilityKeys.SelfManagedPcLaunch),
        };
    }

    /// <summary>
    /// 解析即将写入仓储的候选脚本。通用脚本以候选对象中的源码为准，
    /// 这样更新或清空判断脚本时校验的都是本次提交内容。
    /// </summary>
    public ResolvedScriptSpec ResolveCandidate(ScriptInstance candidate)
    {
        ScriptInstance script = candidate.Clone();
        if (!string.IsNullOrWhiteSpace(script.ExecutionProviderId))
            return ResolveProvider(script);
        if (!string.IsNullOrWhiteSpace(script.ExecutionProviderConfigId))
            return Failed(script, "未声明执行 provider，不能使用 provider 配置身份", "");
        return string.IsNullOrWhiteSpace(script.PluginType)
            ? ResolveGeneric(script, preferInlineSource: true)
            : Resolve(script);
    }

    public ScriptInstance ResolveScript(ScriptInstance declaration)
    {
        return Resolve(declaration).Script;
    }

    private ResolvedScriptSpec ResolveProvider(ScriptInstance script, string userId = "")
    {
        string id = script.ExecutionProviderId?.Trim() ?? "";
        string configId = script.ExecutionProviderConfigId?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(script.PluginType) || !ScriptPathPolicy.IsProviderConfigId(configId))
            return Failed(script, "直驱实例必须单独声明 provider 及其配置身份", "");
        script.ExecutionProviderId = id;
        script.ExecutionProviderConfigId = configId;
        ExecutionProviderDescriptor? provider = _executionProviders?.ResolveExecutionProvider(id);
        if (provider is null)
            return Failed(script, $"执行 provider「{id}」未安装、未启用或尚未完成注册", "");
        if (!string.IsNullOrWhiteSpace(script.MainExe) || !string.IsNullOrWhiteSpace(script.ConfigPath)
            || !string.IsNullOrWhiteSpace(script.LogPath))
            return Failed(script, "直驱实例不能伪造外部脚本主程序或配置路径", "");
        var judge = new ResolvedJudgeScript(false, "javascript", "provider", "", Hash(""));
        try
        {
            // This resolver is a synchronous legacy port. Run the provider's read-only
            // preparation off the caller's UI context; never start a worker here.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var request = new NexusPipeline.Plugin.Abstractions.PluginProviderPrepareRequest(
                configId, userId, script.Id, script.RootPath, "interface.json", "", new());
            var plan = Task.Run(async () => await provider.Provider.PrepareAsync(request, timeout.Token)
                .ConfigureAwait(false), timeout.Token).WaitAsync(timeout.Token).GetAwaiter().GetResult();
            ProviderPlanPolicy.Validate(script.RootPath, plan);
            var frozen = JsonSerializer.Deserialize<NexusPipeline.Plugin.Abstractions.PluginProviderPlan>(
                JsonSerializer.Serialize(plan))!;
            return new ResolvedScriptSpec(script, provider.PluginVersion, judge,
                Hash("provider=" + id + "\nversion=" + provider.PluginVersion + "\nconfig=" + configId
                    + "\nroot=" + script.RootPath + "\nplan=" + JsonSerializer.Serialize(frozen))) { ProviderPlan = frozen };
        }
        catch (Exception ex)
        {
            return Failed(script, "执行 provider 无法准备安全计划：" + ex.GetType().Name + ": " + ex.Message,
                provider.PluginVersion);
        }
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

    private static string ComputeProfileHash(
        ScriptInstance script,
        string pluginName,
        string pluginVersion,
        ResolvedJudgeScript judge,
        string rootProcessRole = "AutomationWorker",
        string outputEncoding = "")
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
        string source = JsonSerializer.Serialize(projection);
        if (rootProcessRole != "AutomationWorker") source += "\nrootProcessRole=" + rootProcessRole;
        if (!string.IsNullOrEmpty(outputEncoding)) source += "\noutputEncoding=" + outputEncoding;
        return Hash(source);
    }

    private static string Hash(string? value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
