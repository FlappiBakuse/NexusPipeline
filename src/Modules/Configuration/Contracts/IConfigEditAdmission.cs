namespace NexusPipeline.Modules.Configuration.Contracts;

/// <summary>配置编辑使用的执行租约与会话门禁端口。</summary>
internal interface IConfigEditAdmission
{
    bool TryExecuteLeaseMutation(
        string scriptId,
        string? userName,
        Action mutation,
        out IReadOnlyList<ExecutionLeaseReference> leases,
        out string? failureCode);

    bool TryBeginEditSession(
        string scriptId,
        string userName,
        string configPath,
        out string? conflict);

    void EndEditSession(string scriptId, string userName);
}
