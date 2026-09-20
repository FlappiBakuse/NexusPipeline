using System.Net;
using NexusPipeline.Modules.Configuration.Editing;
using NexusPipeline.Shared.Results;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Serialization;

namespace NexusPipeline.ControlPlane.Http;

/// <summary>配置编辑 HTTP 适配器：只负责请求解析与响应组装，业务流程由 Application 命令处理。</summary>
internal static class ConfigEditHttpAdapter
{
    internal static Task HandleByUserIdAsync(
        HttpListenerContext context,
        string scriptId,
        string userId,
        string body,
        ConfigEditCommands commands)
    {
        return HandleAsync(context, scriptId, userId, body, commands);
    }

    private static async Task HandleAsync(
        HttpListenerContext context,
        string scriptId,
        string userReference,
        string body,
        ConfigEditCommands commands)
    {
        if (!HttpHelper.IsLoopback(context))
        {
            await HttpHelper.ErrorAsync(context, "local_only", 403).ConfigureAwait(false);
            return;
        }

        var parsed = HttpHelper.ParseBody(body);
        string action = parsed.Get("action").Str();
        string mode = parsed.Get("mode").Str();
        string requesterWindowToken = parsed.Get("requesterWindowToken").Str().Trim();
        string inputName = parsed.Get("configInputName").Str().Trim();
        string inputValue = parsed.Get("configInputValue").Str();
        IReadOnlyDictionary<string, string>? inputOverrides = null;
        if (inputName.Length > 0 || inputValue.Length > 0)
        {
            inputOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [inputName] = inputValue,
            };
        }

        if (action == "start")
        {
            OperationResult<ConfigEditStarted> result =
                commands.Start(
                    scriptId,
                    userReference,
                    mode,
                    inputOverrides: inputOverrides,
                    requesterWindowToken: requesterWindowToken);
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
                commands.Complete(scriptId, userReference, action);
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

        await HttpHelper.ErrorAsync(context, "unknown_action", 400, new { action }).ConfigureAwait(false);
    }
}
