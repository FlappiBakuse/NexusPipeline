using System.Net;
using NexusPipeline.Modules.Execution;

namespace NexusPipeline.ControlPlane.Http;

/// <summary>执行租约与准入冲突的统一 Web 响应。</summary>
internal static class ExecutionConflictResponse
{
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
