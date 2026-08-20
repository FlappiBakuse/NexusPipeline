using NexusPipeline.Models;
using NexusPipeline.Services;

namespace NexusPipeline.Services.Execution;

/// <summary>单次尝试执行宿主端口；实现可以由运行协调器或独立执行器提供。</summary>
internal interface IAttemptExecutionHost
{
    Task<RunAttemptResult?> RunUserScriptCoreAsync(string scriptPath, string role, RunAttempt attempt, CancellationToken token);

    Task<RunAttemptResult> RunAttemptCoreAsync(RunAttempt attempt);
}

/// <summary>
/// 单次尝试执行边界。协调器只通过宿主端口进入兼容实现，
/// 后续可在不改变运行级状态对象和重试策略的情况下替换具体执行器。
/// </summary>
internal sealed class AttemptRunner
{
    private readonly IAttemptExecutionHost _host;

    public AttemptRunner(IAttemptExecutionHost host)
    {
        _host = host;
    }

    public Task<RunAttemptResult?> RunUserScriptAsync(string scriptPath, string role, RunAttempt attempt, CancellationToken token)
    {
        return _host.RunUserScriptCoreAsync(scriptPath, role, attempt, token);
    }

    public Task<RunAttemptResult> RunAsync(RunAttempt attempt)
    {
        return _host.RunAttemptCoreAsync(attempt);
    }
}
