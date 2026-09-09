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
        var args = new Dictionary<string, object?>();
        if (error.Candidates is { Count: > 0 })
        {
            args["candidates"] = error.Candidates;
        }
        if (!string.IsNullOrWhiteSpace(error.CandidateInputName))
        {
            args["inputName"] = error.CandidateInputName;
        }
        if (error.MessageArgs is { Count: > 0 })
        {
            foreach ((string key, object? value) in error.MessageArgs)
            {
                args[key] = value;
            }
        }
        object payload = new
        {
            ok = false,
            code = error.Code,
            args,
        };
        await HttpHelper.WriteJsonAsync(context, payload, status).ConfigureAwait(false);
    }
}
