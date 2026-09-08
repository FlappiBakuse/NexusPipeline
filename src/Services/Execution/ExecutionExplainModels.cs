namespace NexusPipeline.Services.Execution;

/// <summary>运行计划解释的稳定准入失败投影。</summary>
internal sealed record ExecutionExplainAdmissionFailure(
    string Code,
    string Message,
    string Disposition,
    string? ConflictingRunId,
    string? Resource);

internal sealed record ExecutionExplainResources(
    IReadOnlyList<string> ScriptIds,
    IReadOnlyList<string> UserDataKeys,
    IReadOnlyList<string> ExecutablePaths,
    IReadOnlyList<string> ProcessNames,
    IReadOnlyList<string> ConfigPaths,
    IReadOnlyList<string> EmulatorEndpoints,
    IReadOnlyList<string> LogResources,
    IReadOnlyList<string> AuxiliaryExecutablePaths,
    IReadOnlyList<string> AuxiliaryProcessNames);

internal sealed record ExecutionExplainUser(
    string ScriptId,
    string UserId,
    string UserName,
    string Status,
    string ReasonCode,
    string Reason,
    int? SuccessfulRunsToday,
    int MaxSuccessfulRunsPerDay);

internal sealed record ExecutionExplainTask(
    string TaskId,
    int Index,
    string ScriptId,
    string ScriptName,
    int UserCount,
    string? ConfigPath,
    string? LogPath);

/// <summary>
/// 只读运行计划投影。该模型只描述当前快照和准入结果，不代表已注册或已启动运行。
/// </summary>
internal sealed record ExecutionExplainResult(
    string Kind,
    string TargetId,
    string TargetName,
    DateTimeOffset GeneratedAt,
    bool Admissible,
    int TotalTasks,
    string QueueClass,
    string CompletionAction,
    ExecutionExplainAdmissionFailure? AdmissionFailure,
    ExecutionExplainResources Resources,
    IReadOnlyList<ExecutionExplainUser> Users,
    IReadOnlyList<ExecutionExplainTask> Tasks,
    IReadOnlyList<string> Warnings);
