using System.Net;
using NexusPipeline.App.Commands;
using NexusPipeline.App.Contracts;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Web;

/// <summary>配置编辑 HTTP 适配器：只负责请求解析与响应组装，业务流程由 Application 命令处理。</summary>
internal static class ConfigEditHttpAdapter
{
    internal static Task HandleByUserIdAsync(
        HttpListenerContext context,
        string scriptId,
        string userId,
        string body)
    {
        return HandleAsync(context, scriptId, userId, body);
    }

    private static async Task HandleAsync(
        HttpListenerContext context,
        string scriptId,
        string userReference,
        string body)
    {
        if (!HttpHelper.IsLoopback(context))
        {
            await HttpHelper.WriteJsonAsync(
                context,
                new { ok = false, code = "local_only", error = "编辑配置仅支持本机请求" },
                403).ConfigureAwait(false);
            return;
        }

        RuntimeContext ctx = RuntimeContext.Instance;
        var parsed = HttpHelper.ParseBody(body);
        string action = parsed.Get("action").Str();
        string mode = parsed.Get("mode").Str();

        if (action == "start")
        {
            OperationResult<ConfigEditStarted> result =
                ConfigEditCommands.Start(ctx, scriptId, userReference, mode);
            if (!result.Succeeded)
            {
                await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
                return;
            }

            await HttpHelper.WriteJsonAsync(
                context,
                new { ok = true, pid = result.Value!.ProcessId, editMode = result.Value!.EditMode }).ConfigureAwait(false);
            return;
        }

        if (action is "done" or "cancel")
        {
            OperationResult<ConfigEditCompleted> result =
                ConfigEditCommands.Complete(ctx, scriptId, userReference, action);
            if (!result.Succeeded)
            {
                await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
                return;
            }

            await HttpHelper.WriteJsonAsync(
                context,
                new { ok = result.Value!.Success, validation = result.Value.Validation }).ConfigureAwait(false);
            return;
        }

        await HttpHelper.WriteJsonAsync(
            context,
            new { error = "未知操作：" + action },
            400).ConfigureAwait(false);
    }
}
