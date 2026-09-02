using System.Text.Json;
using NexusPipeline.Persistence;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services;

internal interface ISchedulerStateStore
{
    SchedulerPersistedState Load();

    void Save(SchedulerPersistedState state);
}

internal sealed class SchedulerPersistedState
{
    public DateTime? LastSchedulerCheck { get; set; }

    public List<PersistedScheduledOccurrence> Occurrences { get; set; } = new();

    public SchedulerPersistedState Clone()
    {
        return JsonSerializer.Deserialize<SchedulerPersistedState>(
                   JsonSerializer.Serialize(this, JsonOpts.Default),
                   JsonOpts.Default)
               ?? new SchedulerPersistedState();
    }
}

internal sealed class PersistedScheduledOccurrence
{
    public string Key { get; set; } = "";

    public string QueueId { get; set; } = "";

    public string QueueName { get; set; } = "";

    public string OccurrenceKey { get; set; } = "";

    public DateTime OriginalTriggerTime { get; set; }

    public bool IsStartup { get; set; }

    public string Status { get; set; } = "Triggered";

    public int RetryCount { get; set; }

    public string LastReason { get; set; } = "";

    public DateTime NextAttemptAt { get; set; }

    public FrozenQueuePlanData? Plan { get; set; }
}

/// <summary>文件中的冻结执行计划快照。所有引用均为深拷贝，恢复时重新计算运行时资源集合。</summary>
internal sealed class FrozenQueuePlanData
{
    public Models.DispatchQueue Queue { get; set; } = new();

    public List<FrozenQueueTaskData> Tasks { get; set; } = new();

    public FrozenAdmissionProfileData? Admission { get; set; }
}

internal sealed class FrozenQueueTaskData
{
    public Models.QueueTask Task { get; set; } = new();

    public Models.ScriptInstance? Script { get; set; }

    public List<string> EnabledUsers { get; set; } = new();

    /// <summary>冻结的全局用户身份与绑定设置。</summary>
    public List<FrozenResolvedUserData> ResolvedUsers { get; set; } = new();

    /// <summary>触发时解析出的专项 profile 与判断脚本资产指纹；未触发 occurrence 仍会重新解析。</summary>
    public FrozenResolvedScriptSpecData? ResolvedSpec { get; set; }
}

internal sealed class FrozenResolvedScriptSpecData
{
    public string PluginVersion { get; set; } = "";

    public string ProfileHash { get; set; } = "";

    public string? Error { get; set; }

    public FrozenJudgeScriptData JudgeScript { get; set; } = new();

    public static FrozenResolvedScriptSpecData From(ResolvedScriptSpec spec)
    {
        return new FrozenResolvedScriptSpecData
        {
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
        };
    }

    public ResolvedScriptSpec ToRuntime(Models.ScriptInstance script)
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
            Error);
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

    public Models.UserScriptBinding Binding { get; set; } = new();
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

internal sealed class FileSchedulerStateStore : ISchedulerStateStore
{
    private readonly string _path;

    public FileSchedulerStateStore(string? path = null)
    {
        _path = path ?? AppPaths.SchedulerStatePath;
    }

    public SchedulerPersistedState Load()
    {
        if (!File.Exists(_path))
        {
            return new SchedulerPersistedState();
        }
        try
        {
            return JsonSerializer.Deserialize<SchedulerPersistedState>(
                       File.ReadAllText(_path),
                       JsonOpts.Default)
                   ?? new SchedulerPersistedState();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[调度] scheduler-state.json 解析失败，忽略损坏状态：{ex.Message}");
            return new SchedulerPersistedState();
        }
    }

    public void Save(SchedulerPersistedState state)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        JsonUtil.WriteAtomic(_path, JsonSerializer.Serialize(state, JsonOpts.Indented));
    }
}

/// <summary>单元测试用内存状态仓储；多个 Scheduler 实例共享同一对象即可验证重启恢复语义。</summary>
internal sealed class MemorySchedulerStateStore : ISchedulerStateStore
{
    private readonly object _sync = new();

    private SchedulerPersistedState _state = new();

    public SchedulerPersistedState Load()
    {
        lock (_sync)
        {
            return _state.Clone();
        }
    }

    public void Save(SchedulerPersistedState state)
    {
        lock (_sync)
        {
            _state = state.Clone();
        }
    }
}
