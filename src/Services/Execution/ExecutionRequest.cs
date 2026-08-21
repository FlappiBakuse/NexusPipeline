using NexusPipeline.Models;

namespace NexusPipeline.Services.Execution;

/// <summary>执行入口的最小请求模型，可在不启动进程的情况下进行门禁验证。</summary>
internal sealed record ExecutionRequest(string Kind, string TargetId, string Mode, string? UserName = null);

/// <summary>执行门禁结果；Accepted=false 时不应登记运行状态或启动后台任务。</summary>
internal sealed record ExecutionResult(
    bool Accepted,
    string? Error,
    int TotalTasks,
    ScriptInstance? Script,
    DispatchQueue? Queue);
