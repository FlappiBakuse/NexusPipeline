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
        string raw;
        try { raw = File.ReadAllText(_path); }
        catch (FileNotFoundException) { return new SchedulerPersistedState(); }
        catch (DirectoryNotFoundException) { return new SchedulerPersistedState(); }
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !HasExactFields(document.RootElement, [nameof(SchedulerPersistedState.LastSchedulerCheck), nameof(SchedulerPersistedState.Occurrences)]))
            throw new InvalidDataException("调度状态缺少恢复围栏字段");
        var occurrences = document.RootElement.EnumerateObject().Single(p => p.Name.Equals(nameof(SchedulerPersistedState.Occurrences), StringComparison.OrdinalIgnoreCase)).Value;
        if (occurrences.ValueKind != JsonValueKind.Array || occurrences.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object
            || !HasExactFields(item, ["Key", "QueueId", "QueueName", "OccurrenceKey", "OriginalTriggerTime", "IsStartup", "Status", "RetryCount", "LastReason", "NextAttemptAt", "Plan"])))
            throw new InvalidDataException("调度 occurrence 恢复字段缺失或未知");
        var state = JsonSerializer.Deserialize<SchedulerPersistedState>(raw, JsonOpts.Default)
            ?? throw new InvalidDataException("调度状态为空");
        if (state.Occurrences is null || state.Occurrences.Any(item => item is null
            || string.IsNullOrWhiteSpace(item.QueueId) || string.IsNullOrWhiteSpace(item.OccurrenceKey)
            || item.Key != SchedulerTriggerPlanner.TriggerKey(item.QueueId, item.OccurrenceKey)
            || item.OriginalTriggerTime == default
            || item.RetryCount < 0
            || item.Status is not ("Triggered" or "Waiting" or "Running" or "Completed" or "Cancelled" or "Invalidated")))
            throw new InvalidDataException("调度 occurrence 状态无效或未知");
        if (state.Occurrences.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != state.Occurrences.Count)
            throw new InvalidDataException("调度 occurrence 恢复标识重复");
        return state;
    }

    private static bool HasExactFields(JsonElement element, string[] names)
    {
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        return actual.Length == names.Length && new HashSet<string>(actual, StringComparer.OrdinalIgnoreCase).SetEquals(names);
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
