using NexusPipeline.Modules.Queues;

namespace NexusPipeline.Modules.Scheduling.Contracts;

/// <summary>调度器阻止闲时维护的稳定业务原因。</summary>
internal sealed record AutoUpdateIdleBlocker(
    string Code,
    string Message,
    string? QueueName,
    DateTime? TriggerTime);

/// <summary>更新自动化读取调度闲时状态的端口。</summary>
internal interface ISchedulerIdleReader
{
    AutoUpdateIdleBlocker? GetAutoUpdateBlocker(TimeSpan horizon, DateTime? nowOverride = null);
}
