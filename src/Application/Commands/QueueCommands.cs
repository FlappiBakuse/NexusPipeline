using NexusPipeline.App.Contracts;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;

namespace NexusPipeline.App.Commands;

/// <summary>调度队列应用命令；租约协调、校验、落盘和调度重校验集中在服务进程内。</summary>
internal static class QueueCommands
{
    public static OperationResult<DispatchQueue> Create(DispatchQueue candidate, string source = Audit.Web)
    {
        if (string.IsNullOrWhiteSpace(candidate.Name))
        {
            return Validation<DispatchQueue>("队列名称不能为空");
        }

        RuntimeContext ctx = RuntimeContext.Instance;
        try
        {
            NormalizeQueue(candidate);
            string? error = null;
            ctx.Center.WithAdmissionCoordination(() =>
            {
                lock (ctx.DataLock)
                {
                    error = Limits.CheckQueueCount(ctx.Queues.Count)
                        ?? Limits.CheckNameBytes(candidate.Name, Limits.Current.MaxQueueNameBytes, "队列名称")
                        ?? Limits.CheckTimeSets(candidate.TimeSets.Count)
                        ?? CheckTimeFormat(candidate)
                        ?? Limits.CheckQueueTotalUsers(Limits.QueueTotalUsers(ctx, candidate))
                        ?? CheckQueuePluginAvailability(ctx, candidate)
                        ?? Limits.CheckQueueMix(ctx.SnapshotScripts(), candidate);
                    if (error is null)
                    {
                        candidate.Id = Guid.NewGuid().ToString("N");
                        candidate.Index = ctx.Queues.Count == 0 ? 0 : ctx.Queues.Max(item => item.Index) + 1;
                        ctx.Queues.Add(candidate);
                        try
                        {
                            DataStore.SaveQueues(ctx.Queues);
                        }
                        catch
                        {
                            ctx.Queues.Remove(candidate);
                            throw;
                        }
                    }
                }
            });
            if (error is not null)
            {
                return Validation<DispatchQueue>(error);
            }
            ctx.Scheduler.RevalidatePendingPlans();
            Audit.Log(source, "添加调度队列", $"{candidate.Name}（id={candidate.Id}，任务 {candidate.Tasks.Count} 项）");
            return OperationResult<DispatchQueue>.Ok(candidate);
        }
        catch (Exception ex)
        {
            return Internal<DispatchQueue>(ex);
        }
    }

    public static OperationResult<DispatchQueue> Update(
        string queueId,
        DispatchQueue candidate,
        string source = Audit.Web)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        DispatchQueue? existing = ctx.FindQueue(queueId);
        if (existing is null)
        {
            return NotFound<DispatchQueue>($"未找到调度队列：{queueId}");
        }

        try
        {
            string? error = null;
            bool changed = ctx.Center.TryExecuteQueueLeaseMutation(
                queueId,
                () =>
                {
                    lock (ctx.DataLock)
                    {
                        existing = ctx.FindQueue(queueId);
                        error = existing is null
                            ? null
                            : Limits.CheckNameBytes(candidate.Name, Limits.Current.MaxQueueNameBytes, "队列名称")
                                ?? Limits.CheckTimeSets(candidate.TimeSets.Count)
                                ?? CheckTimeFormat(candidate)
                                ?? Limits.CheckQueueTotalUsers(Limits.QueueTotalUsers(ctx, candidate))
                                ?? CheckQueuePluginAvailability(ctx, candidate)
                                ?? Limits.CheckQueueMix(ctx.SnapshotScripts(), candidate);
                        if (existing is not null && error is null)
                        {
                            candidate.Id = existing.Id;
                            candidate.Index = existing.Index;
                            NormalizeQueue(candidate);
                            int index = ctx.Queues.IndexOf(existing);
                            ctx.Queues[index] = candidate;
                            DataStore.SaveQueues(ctx.Queues);
                        }
                    }
                },
                out IReadOnlyList<ExecutionLeaseReference> leases,
                out string? failureCode);
            if (!changed)
            {
                return LeaseConflict<DispatchQueue>(leases, $"queue:{queueId}", failureCode);
            }
            if (existing is null)
            {
                return NotFound<DispatchQueue>($"未找到调度队列：{queueId}");
            }
            if (error is not null)
            {
                return Validation<DispatchQueue>(error);
            }
            ctx.Scheduler.RevalidatePendingPlans();
            Audit.Log(source, "修改调度队列", $"{candidate.Name}（id={candidate.Id}，任务 {candidate.Tasks.Count} 项）");
            return OperationResult<DispatchQueue>.Ok(candidate);
        }
        catch (Exception ex)
        {
            return Internal<DispatchQueue>(ex);
        }
    }

    /// <summary>删除不存在的 ID 仍返回成功，保持既有 Web API 的幂等语义。</summary>
    public static OperationResult<DispatchQueue?> Delete(string queueId, string source = Audit.Web)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        DispatchQueue? removed = null;
        try
        {
            bool changed = ctx.Center.TryExecuteQueueLeaseMutation(
                queueId,
                () =>
                {
                    lock (ctx.DataLock)
                    {
                        removed = ctx.Queues.FirstOrDefault(queue => queue.Id == queueId);
                        ctx.Queues.RemoveAll(queue => queue.Id == queueId);
                        DataStore.SaveQueues(ctx.Queues);
                    }
                    if (removed is not null)
                    {
                        ctx.Plugins.DeleteQueueData(queueId);
                    }
                },
                out IReadOnlyList<ExecutionLeaseReference> leases,
                out string? failureCode);
            if (!changed)
            {
                return LeaseConflict<DispatchQueue?>(leases, $"queue:{queueId}", failureCode);
            }
            ctx.Scheduler.RevalidatePendingPlans();
            Audit.Log(source, "删除调度队列", removed is null ? $"id={queueId}（不存在）" : $"{removed.Name}（id={queueId}）");
            return OperationResult<DispatchQueue?>.Ok(removed);
        }
        catch (Exception ex)
        {
            return Internal<DispatchQueue?>(ex);
        }
    }

    public static OperationResult<bool> Reorder(IReadOnlyList<string>? ids, string source = Audit.Web)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        try
        {
            string? error = null;
            bool changed = ctx.Center.TryExecuteAnyQueueLeaseMutation(
                () =>
                {
                    lock (ctx.DataLock)
                    {
                        if (ids is null || ids.Count != ctx.Queues.Count
                            || ids.Any(string.IsNullOrWhiteSpace)
                            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
                        {
                            error = "队列顺序名单缺失或与当前队列列表不一致";
                        }
                        else
                        {
                            HashSet<string> existing = new(ctx.Queues.Select(queue => queue.Id), StringComparer.Ordinal);
                            if (ids.Any(id => !existing.Contains(id)))
                            {
                                error = "队列顺序名单与当前队列列表不一致";
                            }
                            else
                            {
                                Dictionary<string, DispatchQueue> byId = ctx.Queues.ToDictionary(queue => queue.Id, StringComparer.Ordinal);
                                for (int i = 0; i < ids.Count; i++)
                                {
                                    byId[ids[i]].Index = i;
                                }
                                DataStore.SaveQueues(ctx.Queues);
                            }
                        }
                    }
                },
                out IReadOnlyList<ExecutionLeaseReference> leases,
                out string? failureCode);
            if (!changed)
            {
                return LeaseConflict<bool>(leases, "队列顺序", failureCode);
            }
            if (error is not null)
            {
                return Validation<bool>(error);
            }
            ctx.Scheduler.RevalidatePendingPlans();
            Audit.Log(source, "调整队列顺序", $"{ids!.Count} 个调度队列");
            return OperationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Internal<bool>(ex);
        }
    }

    private static string? CheckTimeFormat(DispatchQueue queue)
    {
        foreach (QueueTimeSet timeSet in queue.TimeSets)
        {
            if (!TimeOnly.TryParseExact(
                    timeSet.Time,
                    "HH:mm",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out _))
            {
                return $"定时时间格式不正确（{timeSet.Time}），须为 HH:mm（如 08:00）";
            }
        }
        return null;
    }

    private static string? CheckQueuePluginAvailability(RuntimeContext ctx, DispatchQueue queue)
    {
        IPluginAvailability plugins = ctx.Resolve<IPluginAvailability>();
        foreach (QueueTask task in queue.Tasks.OrderBy(item => item.Index))
        {
            ScriptInstance? script = ctx.Scripts.FirstOrDefault(item => item.Id == task.ScriptInstanceId);
            if (script is null)
            {
                continue;
            }
            string? unavailableReason = PluginAvailability.GetUnavailableReason(script, plugins);
            if (unavailableReason is not null)
            {
                return unavailableReason + "；请先移除该任务后再保存队列";
            }
        }
        return null;
    }

    private static void NormalizeQueue(DispatchQueue queue)
    {
        if (!QueueRule.IsValidAutoRunMode(queue.AutoRunMode))
        {
            queue.AutoRunMode = "none";
        }
        if (!QueueRule.IsValidCompletionAction(queue.CompletionAction))
        {
            queue.CompletionAction = "none";
        }
        int index = 0;
        foreach (QueueTask task in queue.Tasks.OrderBy(task => task.Index))
        {
            task.Index = index++;
            if (string.IsNullOrWhiteSpace(task.Id))
            {
                task.Id = Guid.NewGuid().ToString("N");
            }
        }
        foreach (QueueTimeSet timeSet in queue.TimeSets)
        {
            if (string.IsNullOrWhiteSpace(timeSet.Id))
            {
                timeSet.Id = Guid.NewGuid().ToString("N");
            }
            if (!TimeOnly.TryParseExact(
                    timeSet.Time,
                    "HH:mm",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out _))
            {
                timeSet.Time = "08:00";
            }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var distinct = new List<QueueTimeSet>();
        foreach (QueueTimeSet timeSet in queue.TimeSets)
        {
            string key = $"{timeSet.Enabled}|{string.Join(",", timeSet.Days.OrderBy(day => day))}|{timeSet.Time}";
            if (seen.Add(key))
            {
                distinct.Add(timeSet);
            }
        }
        queue.TimeSets = distinct;
    }

    private static OperationResult<T> Validation<T>(string message) =>
        OperationResult<T>.Failure("validation_error", message, OperationErrorKind.Validation);

    private static OperationResult<T> NotFound<T>(string message) =>
        OperationResult<T>.Failure("not_found", message, OperationErrorKind.NotFound);

    private static OperationResult<T> LeaseConflict<T>(
        IReadOnlyList<ExecutionLeaseReference> leases,
        string resource,
        string? failureCode = null)
    {
        return failureCode == "host_maintenance"
            ? OperationResult<T>.Failure(
                "host_maintenance",
                "宿主正在进行维护操作，暂不能修改运行配置",
                OperationErrorKind.Conflict)
            : OperationResult<T>.Failure(
                "execution_resource_in_use",
                $"执行计划正在引用资源「{resource}」，当前无法修改；请等待相关运行结束",
                OperationErrorKind.Conflict,
                leases.Select(lease => lease.RunId).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static OperationResult<T> Internal<T>(Exception exception) =>
        OperationResult<T>.Failure("internal_error", exception.Message, OperationErrorKind.Internal);
}
