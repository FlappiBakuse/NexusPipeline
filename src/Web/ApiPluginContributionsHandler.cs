using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.Plugins;
using NexusPipeline.Utilities;

namespace NexusPipeline.Web;

/// <summary>managed-code 插件声明式用户全局设置 API；宿主只处理通用字段与 JSON，不理解插件业务。</summary>
[ApiRoute("plugin-contributions")]
internal static class ApiPluginContributionsHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        if (!TryParseRoute(method, seg, out string userId, out string pluginName, out string contributionId))
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }

        if (RuntimeContext.Instance.FindUser(userId) is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }

        if (method == "GET")
        {
            await ReadAsync(context, userId).ConfigureAwait(false);
            return;
        }
        if (method == "PUT")
        {
            await SaveAsync(
                context,
                userId,
                pluginName,
                contributionId,
                body).ConfigureAwait(false);
            return;
        }
        await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// 解析包含资源名的完整 API 路径。WebServer 只移除 /api，handler 收到的 seg[0] 仍是资源名。
    /// </summary>
    internal static bool TryParseRoute(
        string method,
        string[] seg,
        out string userId,
        out string pluginName,
        out string contributionId)
    {
        userId = "";
        pluginName = "";
        contributionId = "";
        if (seg.Length < 2
            || !seg[0].Equals("plugin-contributions", StringComparison.OrdinalIgnoreCase)
            || !seg[1].Equals("user-global", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (method == "GET" && seg.Length == 3)
        {
            userId = Uri.UnescapeDataString(seg[2]);
            return true;
        }
        if (method == "PUT" && seg.Length == 5)
        {
            userId = Uri.UnescapeDataString(seg[2]);
            pluginName = Uri.UnescapeDataString(seg[3]);
            contributionId = Uri.UnescapeDataString(seg[4]);
            return true;
        }
        return false;
    }

    private static async Task ReadAsync(HttpListenerContext context, string userId)
    {
        PluginManager plugins = RuntimeContext.Instance.Plugins;
        var result = new List<object>();
        foreach (PluginUserGlobalManagementRegistration registration in plugins.UserGlobalManagementContributions)
        {
            JsonObject values;
            try
            {
                values = PluginContributionValidation.SanitizeRead(
                    registration,
                    await registration.Contribution.ReadHandler(userId, CancellationToken.None).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                Logger.Warn($"[插件:{registration.PluginName}] 用户全局设置读取失败：{ex.Message}");
                await PluginErrorAsync(context, "读取插件设置失败").ConfigureAwait(false);
                return;
            }
            result.Add(Project(registration, values));
        }
        await HttpHelper.WriteJsonAsync(context, result).ConfigureAwait(false);
    }

    private static async Task SaveAsync(
        HttpListenerContext context,
        string userId,
        string pluginName,
        string contributionId,
        string body)
    {
        JsonObject? root = HttpHelper.ParseBody(body) as JsonObject;
        JsonObject? values = root?["values"] as JsonObject;
        if (values is null)
        {
            await HttpHelper.WriteJsonAsync(context, new { ok = false, error = "插件设置格式不正确", code = "validation_error" }, 400).ConfigureAwait(false);
            return;
        }
        PluginManager plugins = RuntimeContext.Instance.Plugins;
        if (string.IsNullOrWhiteSpace(pluginName) || !plugins.TryGetUserGlobalManagementContribution(pluginName, contributionId, out PluginUserGlobalManagementRegistration? registration) || registration is null)
        {
            await ContributionNotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        if (!PluginContributionValidation.TryValidateSave(registration, values, out JsonObject sanitized, out string validationError))
        {
            await HttpHelper.WriteJsonAsync(context, new { ok = false, error = validationError, code = "validation_error" }, 400).ConfigureAwait(false);
            return;
        }
        try
        {
            await registration.Contribution.SaveHandler(userId, sanitized, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件:{registration.PluginName}] 用户全局设置保存失败：{ex.Message}");
            await PluginErrorAsync(context, "保存插件设置失败").ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }

    private static object Project(
        PluginUserGlobalManagementRegistration registration,
        JsonObject values)
    {
        return new
        {
            pluginName = registration.PluginName,
            pluginDisplayName = registration.PluginDisplayName,
            id = registration.Contribution.Id,
            title = registration.Contribution.Title,
            description = registration.Contribution.Description,
            order = registration.Contribution.Order,
            fields = registration.Contribution.Fields.Select(field => new
            {
                key = field.Key,
                label = field.Label,
                type = field.Type,
                description = field.Description,
                required = field.Required,
                placeholder = field.Placeholder,
                maxLength = field.MaxLength,
                readOnly = field.ReadOnly || field.Type.Equals("status", StringComparison.OrdinalIgnoreCase),
                options = field.Options?.Select(option => new { value = option.Value, label = option.Label }).ToList(),
            }).ToList(),
            values,
        };
    }

    private static Task ContributionNotFoundAsync(HttpListenerContext context) =>
        HttpHelper.WriteJsonAsync(
            context,
            new { ok = false, error = "插件设置贡献不存在或插件未启用", code = "contribution_not_found" },
            404);

    private static Task PluginErrorAsync(HttpListenerContext context, string message) =>
        HttpHelper.WriteJsonAsync(
            context,
            new { ok = false, error = message, code = "plugin_error" },
            500);
}
