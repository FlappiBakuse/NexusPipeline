namespace NexusPipeline.Modules.Updates;

/// <summary>宿主重启请求的模块级结果；控制面和 Host 生命周期只负责适配，不由 Updates 反向依赖 Host。</summary>
internal sealed record RestartRequestResult(bool Accepted, string Code, string Message, string HandoffId = "")
{
    public static RestartRequestResult Success(string handoffId) =>
        new(true, "ok", "服务重启请求已提交", handoffId);

    public static RestartRequestResult Failure(string code, string message) =>
        new(false, code, message);
}
