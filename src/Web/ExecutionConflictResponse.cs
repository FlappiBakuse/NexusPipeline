using System.Net;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;

namespace NexusPipeline.Web;

/// <summary>执行租约与准入冲突的统一 Web 响应。</summary>
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

        await HttpHelper.ErrorAsync(
            context,
            "execution_resource_in_use",
            409,
            new
            {
                resource,
                runIds = leases.Select(lease => lease.RunId).Distinct(StringComparer.Ordinal).ToArray(),
            }).ConfigureAwait(false);
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
        if (center.TryExecuteLeaseMutation(scriptId, userName, mutation, out IReadOnlyList<ExecutionLeaseReference> leases, out string? _))
        {
            return true;
        }
        await WriteLeaseConflictAsync(context, leases, resource).ConfigureAwait(false);
        return false;
    }

    public static async Task<bool> TryExecuteQueueLeaseMutationAsync(
        HttpListenerContext context,
        DispatchCenter center,
        string queueId,
        string resource,
        Action mutation)
    {
        if (center.TryExecuteQueueLeaseMutation(queueId, mutation, out IReadOnlyList<ExecutionLeaseReference> leases, out string? _))
        {
            return true;
        }
        await WriteLeaseConflictAsync(context, leases, resource).ConfigureAwait(false);
        return false;
    }

    public static async Task<bool> TryExecuteAnyQueueLeaseMutationAsync(
        HttpListenerContext context,
        DispatchCenter center,
        string resource,
        Action mutation)
    {
        if (center.TryExecuteAnyQueueLeaseMutation(mutation, out IReadOnlyList<ExecutionLeaseReference> leases, out string? _))
        {
            return true;
        }
        await WriteLeaseConflictAsync(context, leases, resource).ConfigureAwait(false);
        return false;
    }

    public static async Task WriteAdmissionAsync(HttpListenerContext context, ExecutionAdmissionException exception)
    {
        ExecutionAdmissionFailure failure = exception.Failure;
        await HttpHelper.ErrorAsync(
            context,
            failure.StableCode,
            409,
            new
            {
                resource = failure.Resource,
                conflictingRunId = failure.ConflictingRunId,
                retryable = failure.Disposition == AdmissionFailureDisposition.Transient,
            }).ConfigureAwait(false);
    }
}
