using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Queues.Contracts;
using NexusPipeline.Modules.Scripts.Validation;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Settings.Validation;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Naming;
using NexusPipeline.Shared.Results;
using NexusPipeline.Modules.Queues.Persistence;
using NexusPipeline.Modules.Queues.Validation;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users;

namespace NexusPipeline.Modules.Queues.UseCases;

/// <summary>调度队列应用命令；租约协调、校验、落盘和调度重校验集中在服务进程内。</summary>
internal sealed class QueueCommands
{
    private readonly IQueueRepository _queues;
    private readonly IQueueMutationState _state;
    private readonly IQueueScheduleSnapshotReader _scheduleSnapshot;
    private readonly IQueuePlansChanged _plansChanged;
    private readonly IQueueMutationAdmission _admission;
    private readonly IScriptRepository _scripts;
    private readonly IUserSnapshotReader _users;
    private readonly IPluginAvailability _pluginAvailability;
    private readonly IQueueDataMaintenance _dataMaintenance;

    internal QueueCommands(
        IQueueRepository queues,
        IQueueMutationState state,
        IQueueScheduleSnapshotReader scheduleSnapshot,
        IQueuePlansChanged plansChanged,
        IQueueMutationAdmission admission,
        IScriptRepository scripts,
        IUserSnapshotReader users,
        IPluginAvailability pluginAvailability,
        IQueueDataMaintenance dataMaintenance)
    {
        _queues = queues;
        _state = state;
        _scheduleSnapshot = scheduleSnapshot;
        _plansChanged = plansChanged;
        _admission = admission;
        _scripts = scripts;
        _users = users;
        _pluginAvailability = pluginAvailability;
        _dataMaintenance = dataMaintenance;
    }

    public OperationResult<DispatchQueue> Create(DispatchQueue candidate, string source = Audit.Web)
    {
        if (string.IsNullOrWhiteSpace(candidate.Name))
        {
            return Validation<DispatchQueue>("队列名称不能为空");
        }

        try
        {
            NormalizeQueue(candidate);
            string? error = null;
            bool duplicateName = false;
            QueueMutationAdmissionResult admission = _admission.TryExecuteAny(() =>
            {
                _state.Mutate(queues =>
                {
                    IReadOnlyList<ScriptInstance> scripts = _scripts.Snapshot();
                    IReadOnlyList<NexusUser> users = _users.Snapshot();
                    error = Limits.CheckQueueCount(queues.Count)
                        ?? Limits.CheckNameBytes(candidate.Name, AppFixedLimits.MaxEntityNameBytes, "队列名称");
                    if (error is null && EntityNameRules.HasConflict(queues, candidate.Name, queue => queue.Name))
                    {
                        duplicateName = true;
                        error = "队列名称重复：调度队列已存在同名队列";
                    }
                    error ??= Limits.CheckTimeSets(candidate.TimeSets.Count)
                        ?? CheckTimeFormat(candidate)
                        ?? Limits.CheckQueueTotalUsers(QueueCompositionPolicy.QueueTotalUsers(scripts, users, candidate))
                        ?? CheckQueuePluginAvailability(scripts, candidate)
                        ?? QueueCompositionPolicy.CheckQueueMix(scripts, candidate);
                    if (error is null)
                    {
                        candidate.Id = Guid.NewGuid().ToString("N");
                        candidate.Index = queues.Count == 0 ? 0 : queues.Max(item => item.Index) + 1;
                        queues.Add(candidate);
                        try
                        {
                            QueueDefinitionStore.SaveQueues(queues.ToList());
                        }
                        catch
                        {
                            queues.Remove(candidate);
                            throw;
                        }
                    }
                });
            });
            if (!admission.Allowed)
            {
                return LeaseConflict<DispatchQueue>(admission.RunIds, "队列", admission.FailureCode);
            }
            if (error is not null)
            {
                return duplicateName
                    ? Conflict<DispatchQueue>("duplicate_name", error)
                    : Validation<DispatchQueue>(error);
            }
            _plansChanged.RevalidatePendingPlans();
            Audit.Log(source, "添加调度队列", $"{candidate.Name}（id={candidate.Id}，任务 {candidate.Tasks.Count} 项）");
            return OperationResult<DispatchQueue>.Ok(candidate);
        }
        catch (Exception ex)
        {
            return Internal<DispatchQueue>(ex);
        }
    }

    public OperationResult<DispatchQueue> Update(
        string queueId,
        DispatchQueue candidate,
        string source = Audit.Web)
    {
        DispatchQueue? existing = _queues.FindById(queueId);
        if (existing is null)
        {
            return NotFound<DispatchQueue>($"未找到调度队列：{queueId}");
        }

        try
        {
            string? error = null;
            bool duplicateName = false;
            QueueMutationAdmissionResult admission = _admission.TryExecute(
                queueId,
                () =>
                {
                    _state.Mutate(queues =>
                    {
                        IReadOnlyList<ScriptInstance> scripts = _scripts.Snapshot();
                        IReadOnlyList<NexusUser> users = _users.Snapshot();
                        existing = queues.FirstOrDefault(queue =>
                            string.Equals(queue.Id, queueId, StringComparison.OrdinalIgnoreCase));
                        if (existing is null)
                        {
                            error = null;
                        }
                        else
                        {
                            RemoveDuplicateTasks(candidate);
                            NormalizeTimeSets(candidate);
                            error = Limits.CheckNameBytes(candidate.Name, AppFixedLimits.MaxEntityNameBytes, "队列名称");
                            if (error is null && EntityNameRules.HasConflict(
                                    queues,
                                    candidate.Name,
                                    queue => queue.Name,
                                    queue => string.Equals(queue.Id, existing.Id, StringComparison.OrdinalIgnoreCase)))
                            {
                                duplicateName = true;
                                error = "队列名称重复：调度队列已存在同名队列";
                            }
                            error ??= Limits.CheckTimeSets(candidate.TimeSets.Count)
                                ?? CheckTimeFormat(candidate)
                                ?? Limits.CheckQueueTotalUsers(QueueCompositionPolicy.QueueTotalUsers(scripts, users, candidate))
                                ?? CheckQueuePluginAvailability(scripts, candidate)
                                ?? QueueCompositionPolicy.CheckQueueMix(scripts, candidate);
                        }
                        if (existing is not null && error is null)
                        {
                            candidate.Id = existing.Id;
                            candidate.Index = existing.Index;
                            NormalizeQueue(candidate);
                            int index = queues.ToList().FindIndex(queue =>
                                string.Equals(queue.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
                            if (index >= 0)
                            {
                                queues[index] = candidate.Clone();
                                try
                                {
                                    QueueDefinitionStore.SaveQueues(queues.ToList());
                                }
                                catch
                                {
                                    queues[index] = existing;
                                    throw;
                                }
                            }
                        }
                    });
                });
            if (!admission.Allowed)
            {
                return LeaseConflict<DispatchQueue>(admission.RunIds, $"queue:{queueId}", admission.FailureCode);
            }
            if (existing is null)
            {
                return NotFound<DispatchQueue>($"未找到调度队列：{queueId}");
            }
            if (error is not null)
            {
                return duplicateName
                    ? Conflict<DispatchQueue>("duplicate_name", error)
                    : Validation<DispatchQueue>(error);
            }
            _plansChanged.RevalidatePendingPlans();
            Audit.Log(source, "修改调度队列", $"{candidate.Name}（id={candidate.Id}，任务 {candidate.Tasks.Count} 项）");
            return OperationResult<DispatchQueue>.Ok(candidate);
        }
        catch (Exception ex)
        {
            return Internal<DispatchQueue>(ex);
        }
    }

    /// <summary>删除不存在的 ID 仍返回成功，保持既有 Web API 的幂等语义。</summary>
    public OperationResult<DispatchQueue?> Delete(string queueId, string source = Audit.Web)
    {
        DispatchQueue? removed = null;
        int removedIndex = -1;
        try
        {
            QueueMutationAdmissionResult admission = _admission.TryExecute(
                queueId,
                () =>
                {
                    _state.Mutate(queues =>
                    {
                        removedIndex = queues.ToList().FindIndex(queue => queue.Id == queueId);
                        removed = removedIndex >= 0 ? queues[removedIndex].Clone() : null;
                        while (queues.Any(queue => queue.Id == queueId))
                        {
                            queues.Remove(queues.First(queue => queue.Id == queueId));
                        }
                        try
                        {
                            QueueDefinitionStore.SaveQueues(queues.ToList());
                        }
                        catch
                        {
                            if (removed is not null && removedIndex >= 0)
                            {
                                queues.Insert(Math.Min(removedIndex, queues.Count), removed);
                            }
                            throw;
                        }
                    });
                    if (removed is not null)
                    {
                        _dataMaintenance.RemoveQueueData(queueId);
                    }
                });
            if (!admission.Allowed)
            {
                return LeaseConflict<DispatchQueue?>(admission.RunIds, $"queue:{queueId}", admission.FailureCode);
            }
            _plansChanged.RevalidatePendingPlans();
            Audit.Log(source, "删除调度队列", removed is null ? $"id={queueId}（不存在）" : $"{removed.Name}（id={queueId}）");
            return OperationResult<DispatchQueue?>.Ok(removed);
        }
        catch (Exception ex)
        {
            return Internal<DispatchQueue?>(ex);
        }
    }

    public OperationResult<bool> Reorder(IReadOnlyList<string>? ids, string source = Audit.Web)
    {
        try
        {
            string? error = null;
            QueueMutationAdmissionResult admission = _admission.TryExecuteAny(
                () =>
                {
                    _state.Mutate(queues =>
                    {
                        if (ids is null || ids.Count != queues.Count
                            || ids.Any(string.IsNullOrWhiteSpace)
                            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
                        {
                            error = "队列顺序名单缺失或与当前队列列表不一致";
                        }
                        else
                        {
                            HashSet<string> existing = new(queues.Select(queue => queue.Id), StringComparer.Ordinal);
                            if (ids.Any(id => !existing.Contains(id)))
                            {
                                error = "队列顺序名单与当前队列列表不一致";
                            }
                            else
                            {
                                Dictionary<string, DispatchQueue> byId = queues.ToDictionary(queue => queue.Id, StringComparer.Ordinal);
                                for (int i = 0; i < ids.Count; i++)
                                {
                                    byId[ids[i]].Index = i;
                                }
                                QueueDefinitionStore.SaveQueues(queues.ToList());
                            }
                        }
                    });
                });
            if (!admission.Allowed)
            {
                return LeaseConflict<bool>(admission.RunIds, "队列顺序", admission.FailureCode);
            }
            if (error is not null)
            {
                return Validation<bool>(error);
            }
            _plansChanged.RevalidatePendingPlans();
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

    private string? CheckQueuePluginAvailability(
        IReadOnlyList<ScriptInstance> scripts,
        DispatchQueue queue)
    {
        foreach (QueueTask task in queue.Tasks.OrderBy(item => item.Index))
        {
            ScriptInstance? script = scripts.FirstOrDefault(item => item.Id == task.ScriptInstanceId);
            if (script is null)
            {
                continue;
            }
            string? unavailableReason = PluginAvailability.GetUnavailableReason(script, _pluginAvailability);
            if (unavailableReason is not null)
            {
                return unavailableReason + "；请先移除该任务后再保存队列";
            }
        }
        return null;
    }

    private static void NormalizeQueue(DispatchQueue queue)
    {
        RemoveDuplicateTasks(queue);
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
        NormalizeTimeSets(queue);
    }

    /// <summary>按列表顺序保留同启用状态、同一时间的第一项，并将后续项的星期选择合并到第一项。</summary>
    private static void NormalizeTimeSets(DispatchQueue queue)
    {
        var firstByKey = new Dictionary<(bool Enabled, string Time), QueueTimeSet>();
        var merged = new List<QueueTimeSet>();
        foreach (QueueTimeSet timeSet in queue.TimeSets)
        {
            timeSet.Days ??= new List<int>();
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

            timeSet.Days = timeSet.Days.Distinct().OrderBy(day => day).ToList();
            var key = (timeSet.Enabled, timeSet.Time);
            if (firstByKey.TryGetValue(key, out QueueTimeSet? first))
            {
                first.Days = first.Days
                    .Concat(timeSet.Days)
                    .Distinct()
                    .OrderBy(day => day)
                    .ToList();
                continue;
            }

            firstByKey.Add(key, timeSet);
            merged.Add(timeSet);
        }
        queue.TimeSets = merged;
    }

    /// <summary>按任务排序保留每个脚本实例的第一项，移除后续重复任务。</summary>
    private static void RemoveDuplicateTasks(DispatchQueue queue)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var distinct = new List<QueueTask>();
        foreach (QueueTask task in queue.Tasks.OrderBy(task => task.Index))
        {
            if (!string.IsNullOrWhiteSpace(task.ScriptInstanceId) && !seen.Add(task.ScriptInstanceId))
            {
                continue;
            }
            task.Index = distinct.Count;
            distinct.Add(task);
        }
        queue.Tasks = distinct;
    }

    private static OperationResult<T> Validation<T>(string message) =>
        OperationResult<T>.Failure("validation_error", message, OperationErrorKind.Validation);

    private static OperationResult<T> NotFound<T>(string message) =>
        OperationResult<T>.Failure("not_found", message, OperationErrorKind.NotFound);

    private static OperationResult<T> Conflict<T>(string code, string message) =>
        OperationResult<T>.Failure(code, message, OperationErrorKind.Conflict);

    private static OperationResult<T> LeaseConflict<T>(
        IReadOnlyList<string> runIds,
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
                runIds.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static OperationResult<T> Internal<T>(Exception exception) =>
        OperationResult<T>.Failure("internal_error", exception.Message, OperationErrorKind.Internal);
}
