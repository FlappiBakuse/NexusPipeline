using System.Net;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;

namespace NexusPipeline.Web;

/// <summary>执行租约与准入冲突的统一 Web 响应，保留 error 字段兼容旧客户端。</summary>
internal static class ExecutionConflictResponse
{
    public static async Task<bool> WriteLeaseConflictAsync(
        HttpListenerContext context,
        IReadOnlyList<ExecutionLeaseReference> leases,
        string resource)
    {
        if (leases.Count == 0)
        {
            return false;
        }

        string message = $"执行计划正在引用资源「{resource}」，当前无法修改；请等待相关运行结束";
        await HttpHelper.WriteJsonAsync(
            context,
            new
            {
                ok = false,
                error = message,
                code = "execution_resource_in_use",
                resource,
                runIds = leases.Select(lease => lease.RunId).Distinct(StringComparer.Ordinal).ToArray(),
            },
            409).ConfigureAwait(false);
        return true;
    }

    public static async Task<bool> TryExecuteLeaseMutationAsync(
        HttpListenerContext context,
        DispatchCenter center,
        string scriptId,
        string? userName,
        string resource,
        Action mutation)
    {
        if (center.TryExecuteLeaseMutation(scriptId, userName, mutation, out IReadOnlyList<ExecutionLeaseReference> leases))
        {
            return true;
        }
        await WriteLeaseConflictAsync(context, leases, resource).ConfigureAwait(false);
        return false;
    }

    public static async Task WriteAdmissionAsync(HttpListenerContext context, ExecutionAdmissionException exception)
    {
        ExecutionAdmissionFailure failure = exception.Failure;
        await HttpHelper.WriteJsonAsync(
            context,
            new
            {
                ok = false,
                error = failure.Message,
                code = failure.StableCode,
                resource = failure.Resource,
                conflictingRunId = failure.ConflictingRunId,
                retryable = failure.Disposition == AdmissionFailureDisposition.Transient,
            },
            409).ConfigureAwait(false);
    }
}
