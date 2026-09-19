using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts.Queries;
using NexusPipeline.Modules.Scripts.UseCases;
using NexusPipeline.Modules.Scripts.Validation;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Results;
using NexusPipeline.Shared.Serialization;
using NexusPipeline.Modules.Configuration.Editing;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.ControlPlane.Http;

[ApiRoute("scripts")]
internal static class ApiScriptsHandler
{
    public static async Task Handle(
        HttpListenerContext context,
        string method,
        string[] seg,
        string body,
        ScriptCommands scriptCommands,
        ScriptQueries queries,
        ConfigEditCommands configEditCommands,
        ScriptSaveValidation validation,
        ScriptIconService scriptIcons)
    {
        if (method == "GET" && seg.Length == 1)
        {
            List<ScriptInstance> snapshot = queries.ListEffective().ToList();
            Audit.Log(Audit.Web, "查询脚本实例列表", $"{snapshot.Count} 条");
            await HttpHelper.WriteJsonAsync(context, snapshot).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && seg.Length == 2
            && !seg[1].Equals("edit-sessions", StringComparison.OrdinalIgnoreCase))
        {
            string scriptId = Uri.UnescapeDataString(seg[1]);
            ScriptInstance? script = queries.FindEffective(scriptId);
            if (script is null)
            {
                await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(context, script).ConfigureAwait(false);
            return;
        }
        if (method == "PUT" && seg.Length == 2 && seg[1].Equals("order", StringComparison.OrdinalIgnoreCase))
        {
            await HandleReorderScriptsAsync(context, body, scriptCommands).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && seg.Length == 2 && seg[1].Equals("edit-sessions", StringComparison.OrdinalIgnoreCase))
        {
            var sessions = ConfigEditSessionRegistry.EditSessions.Values
                .Select(session => new
                {
                    scriptId = session.Script.Id,
                    scriptName = session.Script.Name,
                    userName = session.User.UserName,
                    editMode = string.IsNullOrWhiteSpace(session.Mark.EditMode) ? "normal" : session.Mark.EditMode,
                })
                .ToList();
            await HttpHelper.WriteJsonAsync(context, sessions).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && seg.Length == 3 && seg[2].Equals("icon", StringComparison.OrdinalIgnoreCase))
        {
            await HandleIconAsync(context, seg[1], scriptIcons).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 2 && seg[1].Equals("probe", StringComparison.OrdinalIgnoreCase))
        {
            await HandleProbeAsync(context, body, scriptCommands).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 1)
        {
            ScriptInstance? script = HttpHelper.ParseBody<ScriptInstance>(body);
            if (script is null)
            {
                await HttpHelper.ErrorAsync(context, "script_name_required", 400).ConfigureAwait(false);
                return;
            }
            OperationResult<ScriptInstance> result = scriptCommands.Create(script);
            if (!result.Succeeded)
            {
                await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
                return;
            }
            await WriteScriptWithValidationAsync(context, queries.ResolveEffective(result.Value!), validation).ConfigureAwait(false);
            return;
        }
        if (method == "PUT" && seg.Length == 2)
        {
            ScriptInstance? update = HttpHelper.ParseBody<ScriptInstance>(body);
            if (update is null)
            {
                await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                return;
            }
            OperationResult<ScriptInstance> result = scriptCommands.Update(seg[1], update);
            if (!result.Succeeded)
            {
                await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
                return;
            }
            scriptIcons.Invalidate(result.Value!.Id);
            await WriteScriptWithValidationAsync(context, queries.ResolveEffective(result.Value!), validation).ConfigureAwait(false);
            return;
        }
        if (method == "DELETE" && seg.Length == 2)
        {
            OperationResult<ScriptInstance?> result = scriptCommands.Delete(seg[1]);
            if (!result.Succeeded)
            {
                await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
                return;
            }
            scriptIcons.Invalidate(seg[1]);
            ScriptConfigGate.Remove(seg[1]);
            await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
            return;
        }
        await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    /// <summary>专用插件配置探测：前端简化弹窗在根目录填写后即时校验能否推导；inputs 为插件声明的用户输入值。</summary>
    private static async Task HandleProbeAsync(
        HttpListenerContext context,
        string body,
        ScriptCommands scriptCommands)
    {
        JsonNode? node = HttpHelper.ParseBody(body);
        string rootPath = node?["rootPath"]?.ToString() ?? "";
        string pluginType = node?["pluginType"]?.ToString() ?? "";
        Dictionary<string, string>? inputs = null;
        if (node?["inputs"] is JsonObject inputObject)
        {
            inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, JsonNode?> item in inputObject)
            {
                if (item.Value is not null)
                {
                    inputs[item.Key] = item.Value.ToString();
                }
            }
        }
        OperationResult<ScriptProfile> result = scriptCommands.Probe(pluginType, rootPath, inputs);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { ok = true, profile = result.Value }).ConfigureAwait(false);
    }

    /// <summary>保存（新建/更新）脚本实例的响应：顶层保留完整 ScriptInstance 字段，专项脚本额外附带
    /// script-save 校验结果（validation；含角落通知），前端据此提醒而不改变既有字段读取。</summary>
    private static async Task WriteScriptWithValidationAsync(
        HttpListenerContext context,
        ScriptInstance script,
        ScriptSaveValidation validation)
    {
        ConfigValidationResult? result = await validation.RunForScriptAsync(script).ConfigureAwait(false);
        if (result is null)
        {
            await HttpHelper.WriteJsonAsync(context, script).ConfigureAwait(false);
            return;
        }
        var node = System.Text.Json.JsonSerializer.SerializeToNode(script, JsonOpts.Web) as JsonObject ?? new JsonObject();
        node["validation"] = System.Text.Json.JsonSerializer.SerializeToNode(result, JsonOpts.Web);
        await HttpHelper.WriteJsonAsync(context, node).ConfigureAwait(false);
    }

    /// <summary>脚本主程序图标响应；图标提取、脚本目标校验和缓存由 ScriptIconService 负责。</summary>
    private static async Task HandleIconAsync(
        HttpListenerContext context,
        string scriptId,
        ScriptIconService scriptIcons)
    {
        byte[]? icon = scriptIcons.Get(scriptId);
        if (icon is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        context.Response.StatusCode = 200;
        context.Response.ContentType = "image/png";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.ContentLength64 = icon.Length;
        await context.Response.OutputStream.WriteAsync(icon).ConfigureAwait(false);
        context.Response.OutputStream.Close();
    }

    private static string ResolveLaunchTargetExe(ScriptInstance script)
    {
        string workingDir = string.IsNullOrWhiteSpace(script.RootPath)
            ? Path.GetDirectoryName(script.MainExe) ?? ""
            : script.RootPath;
        return SystemActions.ResolveLaunchTarget(script.MainExe, workingDir, script.Args).ExePath;
    }

    /// <summary>脚本实例顺序重排：请求体携带完整 id 名单，与现有集合完全一致时按新顺序重赋 Index 落盘。</summary>
    private static async Task HandleReorderScriptsAsync(
        HttpListenerContext context,
        string body,
        ScriptCommands scriptCommands)
    {
        JsonNode? node = HttpHelper.ParseBody(body);
        List<string>? ids = node?["ids"] is JsonArray array
            ? array.Select(item => item?.ToString() ?? "").ToList()
            : null;
        OperationResult<bool> result = scriptCommands.Reorder(ids);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }

}
