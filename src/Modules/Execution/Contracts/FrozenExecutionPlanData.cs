using System.Text.Json;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Users;

namespace NexusPipeline.Modules.Execution.Contracts;

/// <summary>文件中的冻结执行计划快照。所有引用均为深拷贝，恢复时重新计算运行时资源集合。</summary>
internal sealed class FrozenQueuePlanData
{
    public DispatchQueue Queue { get; set; } = new();

    public List<FrozenQueueTaskData> Tasks { get; set; } = new();

    public FrozenAdmissionProfileData? Admission { get; set; }
}

internal sealed class FrozenQueueTaskData
{
    public QueueTask Task { get; set; } = new();

    public ScriptInstance? Script { get; set; }

    public List<string> EnabledUsers { get; set; } = new();

    /// <summary>冻结的全局用户身份与绑定设置。</summary>
    public List<FrozenResolvedUserData> ResolvedUsers { get; set; } = new();

    /// <summary>触发时解析出的专项 profile 与判断脚本资产指纹；未触发 occurrence 仍会重新解析。</summary>
    public FrozenResolvedScriptSpecData? ResolvedSpec { get; set; }
}

internal sealed class FrozenResolvedScriptSpecData
{
    public TaskProtocolDescriptor? TaskProtocol { get; set; }

    public string PluginVersion { get; set; } = "";

    public string ProfileHash { get; set; } = "";

    public string? Error { get; set; }

    public FrozenJudgeScriptData JudgeScript { get; set; } = new();

    public List<string> ExtraConfigPaths { get; set; } = new();

    public List<string> ConfigInputCandidates { get; set; } = new();

    public string ConfigInputName { get; set; } = "";

    public bool SelfManagedPcLaunch { get; set; }

    public static FrozenResolvedScriptSpecData From(ResolvedScriptSpec spec)
    {
        return new FrozenResolvedScriptSpecData
        {
            TaskProtocol = spec.TaskProtocol is {} protocol ? protocol with
            {
                ReadResources = protocol.ReadResources.ToArray(),
                ConfigRules = protocol.ConfigRules.ToArray(),
                EnvironmentChecks = protocol.EnvironmentChecks.Select(check => check with
                {
                    Selector = check.Selector is null ? null : (System.Text.Json.Nodes.JsonArray)check.Selector.DeepClone(),
                }).ToArray(),
            } : null,
            PluginVersion = spec.PluginVersion,
            ProfileHash = spec.ProfileHash,
            Error = spec.Error,
            JudgeScript = new FrozenJudgeScriptData
            {
                Enabled = spec.JudgeScript.Enabled,
                Language = spec.JudgeScript.Language,
                SourceKind = spec.JudgeScript.SourceKind,
                SourcePath = spec.JudgeScript.SourcePath,
                ContentHash = spec.JudgeScript.ContentHash,
            },
            ExtraConfigPaths = spec.ExtraConfigPaths.ToList(),
            ConfigInputCandidates = spec.ConfigInputCandidates.ToList(),
            ConfigInputName = spec.ConfigInputName,
            SelfManagedPcLaunch = spec.SelfManagedPcLaunch,
        };
    }

    public ResolvedScriptSpec ToRuntime(ScriptInstance script)
    {
        return new ResolvedScriptSpec(
            script,
            PluginVersion,
            new ResolvedJudgeScript(
                JudgeScript.Enabled,
                JudgeScript.Language,
                JudgeScript.SourceKind,
                JudgeScript.SourcePath,
                JudgeScript.ContentHash),
            ProfileHash,
            Error)
        {
            TaskProtocol = TaskProtocol is {} protocol ? protocol with
            {
                ReadResources = protocol.ReadResources.ToArray(),
                ConfigRules = protocol.ConfigRules.ToArray(),
                EnvironmentChecks = protocol.EnvironmentChecks.Select(check => check with
                {
                    Selector = check.Selector is null ? null : (System.Text.Json.Nodes.JsonArray)check.Selector.DeepClone(),
                }).ToArray(),
            } : null,
            ExtraConfigPaths = ExtraConfigPaths,
            ConfigInputCandidates = ConfigInputCandidates,
            ConfigInputName = ConfigInputName,
            SelfManagedPcLaunch = SelfManagedPcLaunch,
        };
    }
}

internal sealed class FrozenJudgeScriptData
{
    public bool Enabled { get; set; }

    public string Language { get; set; } = "javascript";

    public string SourceKind { get; set; } = "";

    public string SourcePath { get; set; } = "";

    public string ContentHash { get; set; } = "";
}

internal sealed class FrozenResolvedUserData
{
    public string UserId { get; set; } = "";

    public string UserName { get; set; } = "";

    public UserScriptBinding Binding { get; set; } = new();

    /// <summary>按该用户 ConfigInputs 解析的专项快照；未设置输入值时为空（沿用共享 ResolvedSpec）。</summary>
    public FrozenResolvedScriptSpecData? Spec { get; set; }
}

internal sealed class FrozenAdmissionProfileData
{
    public string Kind { get; set; } = "queue";

    public string? QueueClass { get; set; }

    public string CompletionAction { get; set; } = "none";

    public List<string> ScriptIds { get; set; } = new();

    public List<string> UserDataKeys { get; set; } = new();

    public List<string> ExecutablePaths { get; set; } = new();

    public List<string> ProcessNames { get; set; } = new();

    public List<string> ConfigPaths { get; set; } = new();

    public List<string> EmulatorEndpoints { get; set; } = new();

    public List<FrozenLogResourceData> LogResources { get; set; } = new();

    public List<string> AuxiliaryExecutablePaths { get; set; } = new();

    public List<string> AuxiliaryProcessNames { get; set; } = new();
}

internal sealed class FrozenLogResourceData
{
    public string BaseDirectory { get; set; } = "";

    public string Pattern { get; set; } = "";

    public bool IsExactFile { get; set; }

    public string DisplayPath { get; set; } = "";
}
