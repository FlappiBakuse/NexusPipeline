using System.Net;
using NexusPipeline.App.Contracts;

namespace NexusPipeline.Web;

/// <summary>将无 HTTP 依赖的应用错误适配为 Web API 响应。</summary>
internal static class ApplicationErrorResponse
{
    public static async Task WriteAsync(HttpListenerContext context, OperationError error)
    {
        int status = error.Kind switch
        {
            OperationErrorKind.NotFound => 404,
            OperationErrorKind.Conflict => 409,
            OperationErrorKind.Forbidden => 403,
            OperationErrorKind.Unavailable => 503,
            OperationErrorKind.Timeout => 504,
            OperationErrorKind.Internal => 500,
            _ => 400,
        };
        object payload = error.Candidates is { Count: > 0 }
            ? new
            {
                ok = false,
                error = error.Message,
                code = error.Code,
                candidates = error.Candidates,
            }
            : new
            {
                ok = false,
                error = error.Message,
                code = error.Code,
            };
        await HttpHelper.WriteJsonAsync(context, payload, status).ConfigureAwait(false);
    }
}
