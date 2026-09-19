using System.Text.Json;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Execution.Contracts;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.Modules.Scheduling;

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

    internal int SaveCount { get; private set; }

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
            SaveCount++;
            _state = state.Clone();
        }
    }
}
