namespace NexusPipeline.Services.Execution;

internal enum ExecutionAdmissionFailureCode
{
    DuplicateTarget,
    StandardQueueAlreadyRunning,
    ResourceConflict,
    CompletionActionConflict,
    PendingSystemAction,
    ExecutionGroupClosing,
    ProcessConflict,
    HostMaintenance,
}

internal enum AdmissionFailureDisposition
{
    Permanent,
    Transient,
}

internal sealed record ExecutionAdmissionFailure(
    ExecutionAdmissionFailureCode Code,
    string Message,
    string? ConflictingRunId = null,
    string? Resource = null)
{
    public AdmissionFailureDisposition Disposition => Code switch
    {
        ExecutionAdmissionFailureCode.DuplicateTarget
            or ExecutionAdmissionFailureCode.StandardQueueAlreadyRunning
            or ExecutionAdmissionFailureCode.ResourceConflict
            or ExecutionAdmissionFailureCode.PendingSystemAction
            or ExecutionAdmissionFailureCode.ExecutionGroupClosing
            or ExecutionAdmissionFailureCode.ProcessConflict
            or ExecutionAdmissionFailureCode.HostMaintenance => AdmissionFailureDisposition.Transient,
        _ => AdmissionFailureDisposition.Permanent,
    };

    public string StableCode => Code switch
    {
        ExecutionAdmissionFailureCode.DuplicateTarget => "duplicate_target",
        ExecutionAdmissionFailureCode.StandardQueueAlreadyRunning => "standard_queue_already_running",
        ExecutionAdmissionFailureCode.ResourceConflict => "execution_resource_in_use",
        ExecutionAdmissionFailureCode.CompletionActionConflict => "completion_action_conflict",
        ExecutionAdmissionFailureCode.PendingSystemAction => "pending_system_action",
        ExecutionAdmissionFailureCode.ExecutionGroupClosing => "execution_group_closing",
        ExecutionAdmissionFailureCode.ProcessConflict => "process_conflict",
        ExecutionAdmissionFailureCode.HostMaintenance => "host_maintenance",
        _ => "execution_admission_failed",
    };
}

/// <summary>
/// 并行准入纯策略：只比较候选 profile、活动 profile 和未执行完成意图，禁止 IO、RuntimeContext 与可变状态。
/// </summary>
internal sealed class ExecutionAdmissionPolicy
{
    public ExecutionAdmissionFailure? Evaluate(
        string candidateKind,
        string candidateTargetId,
        string candidateTargetName,
        ExecutionAdmissionProfile candidate,
        IReadOnlyCollection<ExecutionAdmissionEntry> active,
        IReadOnlyCollection<CompletionIntent> outstandingIntents)
    {
        ExecutionAdmissionEntry? duplicate = active.FirstOrDefault(entry =>
            entry.Kind == candidateKind
            && string.Equals(entry.TargetId, candidateTargetId, StringComparison.Ordinal));
        if (duplicate is not null)
        {
            string message = candidateKind == "queue"
                ? $"调度队列「{candidateTargetName}」正在运行，请先完成后再执行"
                : $"脚本「{candidateTargetName}」正在运行，请先退出后再执行";
            return new ExecutionAdmissionFailure(
                ExecutionAdmissionFailureCode.DuplicateTarget,
                message,
                duplicate.RunId);
        }

        if (candidate.Kind == "queue"
            && candidate.QueueClass == ExecutionConcurrencyClass.Standard)
        {
            ExecutionAdmissionEntry? standard = active.FirstOrDefault(entry =>
                entry.Kind == "queue"
                && entry.Profile.QueueClass == ExecutionConcurrencyClass.Standard);
            if (standard is not null)
            {
                return new ExecutionAdmissionFailure(
                    ExecutionAdmissionFailureCode.StandardQueueAlreadyRunning,
                    $"已有其他调度队列正在运行，当前队列「{candidateTargetName}」暂不能并行执行",
                    standard.RunId);
            }
        }

        foreach (ExecutionAdmissionEntry entry in active)
        {
            string? resource = candidate.Resources.FindConflict(entry.Profile.Resources);
            if (resource is not null)
            {
                return new ExecutionAdmissionFailure(
                    ExecutionAdmissionFailureCode.ResourceConflict,
                    $"当前执行与运行中的「{entry.TargetName}」存在资源冲突（{resource}）",
                    entry.RunId,
                    resource);
            }
        }

        if (candidate.Kind == "queue")
        {
            string candidateAction = ExecutionAdmissionProfile.NormalizeCompletionAction(candidate.CompletionAction);
            if (candidateAction != "none")
            {
                string? conflictingAction = active
                    .Where(entry => entry.Kind == "queue")
                    .Select(entry => ExecutionAdmissionProfile.NormalizeCompletionAction(entry.Profile.CompletionAction))
                    .Concat(outstandingIntents.Select(intent => ExecutionAdmissionProfile.NormalizeCompletionAction(intent.Action)))
                    .FirstOrDefault(action => action != "none" && !string.Equals(action, candidateAction, StringComparison.OrdinalIgnoreCase));
                if (conflictingAction is not null)
                {
                    return new ExecutionAdmissionFailure(
                        ExecutionAdmissionFailureCode.CompletionActionConflict,
                        $"并行运行组已有完成操作「{QueueActionText(conflictingAction)}」，当前队列的完成操作「{QueueActionText(candidateAction)}」不兼容",
                        Resource: $"completion:{conflictingAction}");
                }
            }
        }

        return null;
    }

    private static string QueueActionText(string action)
    {
        return action switch
        {
            "exit" => "退出软件",
            "sleep" => "休眠",
            "reboot" => "重启",
            "shutdown" => "关机",
            _ => "无操作",
        };
    }
}

internal sealed class ExecutionAdmissionException : InvalidOperationException
{
    public ExecutionAdmissionFailure Failure { get; }

    public ExecutionAdmissionException(ExecutionAdmissionFailure failure)
        : base(failure.Message)
    {
        Failure = failure;
    }
}
