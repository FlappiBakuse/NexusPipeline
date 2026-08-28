using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Plugins;
using NexusPipeline.Utilities;

namespace NexusPipeline.Web;

/// <summary>managed-code 插件声明式用户全局设置 API；宿主只处理通用字段与 JSON，不理解插件业务。</summary>
[ApiRoute("plugin-contributions")]
internal static class ApiPluginContributionsHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        if (TryParseUiQueryRoute(method, seg))
        {
            await QueryUiAsync(context, body).ConfigureAwait(false);
            return;
        }
        if (TryParseUiActionRoute(method, seg, out string actionPluginName, out string actionContributionId, out string action))
        {
            await RunUiActionAsync(context, actionPluginName, actionContributionId, action, body).ConfigureAwait(false);
            return;
        }
        if (TryParseUiSaveRoute(method, seg, out string savePluginName, out string saveContributionId))
        {
            await SaveUiAsync(context, savePluginName, saveContributionId, body).ConfigureAwait(false);
            return;
        }
        if (TryParseUserListBadgesRoute(method, seg))
        {
            await ReadUserListBadgesAsync(context).ConfigureAwait(false);
            return;
        }
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

    internal static bool TryParseUiQueryRoute(string method, string[] seg)
    {
        return method == "POST"
            && seg.Length == 3
            && seg[0].Equals("plugin-contributions", StringComparison.OrdinalIgnoreCase)
            && seg[1].Equals("ui", StringComparison.OrdinalIgnoreCase)
            && seg[2].Equals("query", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryParseUiSaveRoute(
        string method,
        string[] seg,
        out string pluginName,
        out string contributionId)
    {
        pluginName = "";
        contributionId = "";
        if (method != "PUT"
            || seg.Length != 4
            || !seg[0].Equals("plugin-contributions", StringComparison.OrdinalIgnoreCase)
            || !seg[1].Equals("ui", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        pluginName = Uri.UnescapeDataString(seg[2]);
        contributionId = Uri.UnescapeDataString(seg[3]);
        return true;
    }

    internal static bool TryParseUiActionRoute(
        string method,
        string[] seg,
        out string pluginName,
        out string contributionId,
        out string action)
    {
        pluginName = "";
        contributionId = "";
        action = "";
        if (method != "POST"
            || seg.Length != 6
            || !seg[0].Equals("plugin-contributions", StringComparison.OrdinalIgnoreCase)
            || !seg[1].Equals("ui", StringComparison.OrdinalIgnoreCase)
            || !seg[4].Equals("action", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        pluginName = Uri.UnescapeDataString(seg[2]);
        contributionId = Uri.UnescapeDataString(seg[3]);
        action = Uri.UnescapeDataString(seg[5]);
        return true;
    }

    internal static bool TryParseUserListBadgesRoute(string method, string[] seg)
    {
        return method == "GET"
            && seg.Length == 2
            && seg[0].Equals("plugin-contributions", StringComparison.OrdinalIgnoreCase)
            && seg[1].Equals("user-list-badges", StringComparison.OrdinalIgnoreCase);
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

    private static async Task QueryUiAsync(HttpListenerContext context, string body)
    {
        JsonObject? root = HttpHelper.ParseBody(body) as JsonObject;
        string slot = ReadString(root?["slot"]);
        if (root is null || !PluginUiSlots.All.Contains(slot) || root["contexts"] is not JsonArray contexts || contexts.Count is < 1 or > 256)
        {
            await UiValidationErrorAsync(context, "插件 UI 查询参数无效").ConfigureAwait(false);
            return;
        }

        var parsedContexts = new List<PluginUiContext>(contexts.Count);
        foreach (JsonNode? item in contexts)
        {
            if (item is not JsonObject contextObject)
            {
                await UiValidationErrorAsync(context, "插件 UI 上下文无效").ConfigureAwait(false);
                return;
            }
            if (!TryCreateUiContext(contextObject, slot, out PluginUiContext uiContext, out string error))
            {
                await UiValidationErrorAsync(context, error.Length == 0 ? "插件 UI 上下文无效" : error).ConfigureAwait(false);
                return;
            }
            parsedContexts.Add(uiContext);
        }

        PluginManager plugins = RuntimeContext.Instance.Plugins;
        var result = new List<object>();
        foreach (PluginUiContributionRegistration registration in plugins.UiContributions.Where(item =>
                     string.Equals(item.Contribution.Slot, slot, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (PluginUiContext uiContext in parsedContexts)
            {
                JsonObject values = new();
                if (registration.Contribution.ReadHandler is not null)
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        JsonObject? payload = await registration.Contribution.ReadHandler(uiContext, timeout.Token)
                            .AsTask()
                            .WaitAsync(timeout.Token)
                            .ConfigureAwait(false);
                        if (!PluginUiValidation.TrySanitizeRead(registration.Contribution, payload, out values, out string error))
                        {
                            Logger.Warn($"[插件:{registration.PluginName}] UI 贡献读取内容无效（{registration.Contribution.Id}）：{error}");
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[插件:{registration.PluginName}] UI 贡献读取失败（{registration.Contribution.Id}）：{ex.Message}");
                        continue;
                    }
                }

                result.Add(ProjectUiContribution(registration, uiContext, values));
            }
        }
        await HttpHelper.WriteJsonAsync(context, new { slot, contributions = result }).ConfigureAwait(false);
    }

    private static async Task SaveUiAsync(
        HttpListenerContext context,
        string pluginName,
        string contributionId,
        string body)
    {
        JsonObject? root = HttpHelper.ParseBody(body) as JsonObject;
        if (!TryParseUiContextFromBody(root, out PluginUiContext uiContext, out JsonObject? values, out string error))
        {
            await UiValidationErrorAsync(context, error).ConfigureAwait(false);
            return;
        }
        PluginManager plugins = RuntimeContext.Instance.Plugins;
        if (!plugins.TryGetUiContribution(pluginName, contributionId, out PluginUiContributionRegistration? registration)
            || registration is null
            || !string.Equals(registration.Contribution.Slot, uiContext.Slot, StringComparison.OrdinalIgnoreCase))
        {
            await UiContributionNotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        if (registration.Contribution.SaveHandler is null)
        {
            await HttpHelper.WriteJsonAsync(context, new { ok = false, code = "action_not_supported", error = "该插件 UI 贡献不支持保存" }, 405).ConfigureAwait(false);
            return;
        }
        if (!PluginUiValidation.TryValidateValues(registration.Contribution, values, out error))
        {
            await UiValidationErrorAsync(context, error).ConfigureAwait(false);
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await registration.Contribution.SaveHandler(
                    uiContext,
                    (JsonObject)values!.DeepClone(),
                    timeout.Token)
                .AsTask()
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件:{registration.PluginName}] UI 贡献保存失败（{registration.Contribution.Id}）：{ex.Message}");
            await UiPluginErrorAsync(context, "保存插件 UI 设置失败").ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }

    private static async Task RunUiActionAsync(
        HttpListenerContext context,
        string pluginName,
        string contributionId,
        string action,
        string body)
    {
        if (!IsSafeAction(action))
        {
            await UiValidationErrorAsync(context, "插件 UI 动作名无效").ConfigureAwait(false);
            return;
        }
        JsonObject? root = HttpHelper.ParseBody(body) as JsonObject;
        if (!TryParseUiContextFromBody(root, out PluginUiContext uiContext, out JsonObject? values, out string error))
        {
            await UiValidationErrorAsync(context, error).ConfigureAwait(false);
            return;
        }
        PluginManager plugins = RuntimeContext.Instance.Plugins;
        if (!plugins.TryGetUiContribution(pluginName, contributionId, out PluginUiContributionRegistration? registration)
            || registration is null
            || !string.Equals(registration.Contribution.Slot, uiContext.Slot, StringComparison.OrdinalIgnoreCase))
        {
            await UiContributionNotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        if (registration.Contribution.ActionHandler is null)
        {
            await HttpHelper.WriteJsonAsync(context, new { ok = false, code = "action_not_supported", error = "该插件 UI 贡献不支持此动作" }, 405).ConfigureAwait(false);
            return;
        }
        if (!PluginUiValidation.TryValidateValues(registration.Contribution, values, out error))
        {
            await UiValidationErrorAsync(context, error).ConfigureAwait(false);
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            JsonObject? payload = await registration.Contribution.ActionHandler(
                    uiContext,
                    action,
                    (JsonObject)values!.DeepClone(),
                    timeout.Token)
                .AsTask()
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
            if (!PluginUiValidation.TrySanitizeRead(registration.Contribution, payload, out JsonObject sanitized, out error))
            {
                Logger.Warn($"[插件:{registration.PluginName}] UI 动作返回内容无效（{registration.Contribution.Id}/{action}）：{error}");
                await UiPluginErrorAsync(context, "插件 UI 动作返回无效内容").ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(context, new { ok = true, value = sanitized }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件:{registration.PluginName}] UI 动作执行失败（{registration.Contribution.Id}/{action}）：{ex.Message}");
            await UiPluginErrorAsync(context, "执行插件 UI 动作失败").ConfigureAwait(false);
        }
    }

    private static bool TryParseUiContextFromBody(
        JsonObject? root,
        out PluginUiContext uiContext,
        out JsonObject? values,
        out string error)
    {
        uiContext = new PluginUiContext("");
        values = null;
        error = "插件 UI 请求格式不正确";
        if (root?["context"] is not JsonObject contextObject || root["values"] is not JsonObject valueObject)
        {
            return false;
        }
        string slot = ReadString(contextObject["slot"]);
        if (!PluginUiSlots.All.Contains(slot))
        {
            error = "插件 UI slot 不受支持";
            return false;
        }
        if (!TryCreateUiContext(contextObject, slot, out uiContext, out error))
        {
            return false;
        }
        values = valueObject;
        return true;
    }

    private static bool TryCreateUiContext(
        JsonObject source,
        string slot,
        out PluginUiContext uiContext,
        out string error)
    {
        string mode = ReadString(source["mode"]);
        string primaryId = ReadString(source["primaryId"]);
        string secondaryId = ReadString(source["secondaryId"]);
        if (!IsSafeContextValue(mode) || !IsSafeContextValue(primaryId) || !IsSafeContextValue(secondaryId))
        {
            uiContext = new PluginUiContext(slot);
            error = "插件 UI 上下文值无效";
            return false;
        }
        uiContext = new PluginUiContext(slot, mode, primaryId, secondaryId);
        error = "";
        return true;
    }

    private static object ProjectUiContribution(
        PluginUiContributionRegistration registration,
        PluginUiContext uiContext,
        JsonObject values)
    {
        PluginUiContribution contribution = registration.Contribution;
        return new
        {
            pluginName = registration.PluginName,
            pluginDisplayName = registration.PluginDisplayName,
            id = contribution.Id,
            slot = contribution.Slot,
            kind = contribution.Kind,
            title = contribution.Title,
            description = contribution.Description,
            order = contribution.Order,
            fields = ProjectUiFields(contribution),
            context = new
            {
                slot = uiContext.Slot,
                mode = uiContext.Mode,
                primaryId = uiContext.PrimaryId,
                secondaryId = uiContext.SecondaryId,
            },
            values,
        };
    }

    private static object[] ProjectUiFields(PluginUiContribution contribution)
    {
        return (contribution.Fields ?? Array.Empty<PluginUiField>())
            .Select(field => new
            {
                key = field.Key,
                label = field.Label,
                type = field.Type,
                description = field.Description,
                required = field.Required,
                placeholder = field.Placeholder,
                maxLength = field.MaxLength,
                readOnly = field.ReadOnly || field.Type.Equals("status", StringComparison.OrdinalIgnoreCase),
                min = field.Min,
                max = field.Max,
                step = field.Step,
                options = field.Options?.Select(option => new { value = option.Value, label = option.Label }).ToList(),
            })
            .Cast<object>()
            .ToArray();
    }

    private static string ReadString(JsonNode? node)
    {
        if (node is null) return "";
        try
        {
            return node.GetValue<string>()?.Trim() ?? "";
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    private static bool IsSafeContextValue(string value)
    {
        return value.Length <= 256 && value.All(character => !char.IsControl(character));
    }

    private static bool IsSafeAction(string action)
    {
        return action.Length is > 0 and <= 64
            && action.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
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

    private static async Task ReadUserListBadgesAsync(HttpListenerContext context)
    {
        PluginManager plugins = RuntimeContext.Instance.Plugins;
        IReadOnlyList<PluginUserListBadgeRegistration> contributions = plugins.UserListBadgeContributions;
        List<NexusUser> users = RuntimeContext.Instance.SnapshotUsers()
            .OrderBy(user => user.Index)
            .ToList();
        var result = new List<object>(users.Count);
        foreach (NexusUser user in users)
        {
            var badges = new List<object>();
            foreach (PluginUserListBadgeRegistration registration in contributions)
            {
                PluginUserListBadge? badge;
                try
                {
                    badge = await registration.Contribution.ReadHandler(user.Id, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[插件:{registration.PluginName}] 用户列表徽章读取失败（{user.Id}）：{ex.Message}");
                    continue;
                }
                if (!PluginContributionValidation.TrySanitizeUserListBadge(badge, out PluginUserListBadge? sanitized, out string error))
                {
                    Logger.Warn($"[插件:{registration.PluginName}] 用户列表徽章无效（{registration.Contribution.Id}/{user.Id}）：{error}");
                    continue;
                }
                if (sanitized is null)
                {
                    continue;
                }
                badges.Add(new
                {
                    pluginName = registration.PluginName,
                    pluginDisplayName = registration.PluginDisplayName,
                    id = registration.Contribution.Id,
                    label = sanitized.Label,
                    tone = sanitized.Tone,
                    title = sanitized.Title,
                    order = registration.Contribution.Order,
                });
            }
            result.Add(new { userId = user.Id, badges });
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

    private static Task UiValidationErrorAsync(HttpListenerContext context, string message) =>
        HttpHelper.WriteJsonAsync(
            context,
            new { ok = false, error = message, code = "validation_error" },
            400);

    private static Task UiContributionNotFoundAsync(HttpListenerContext context) =>
        HttpHelper.WriteJsonAsync(
            context,
            new { ok = false, error = "插件 UI 贡献不存在或插件未启用", code = "contribution_not_found" },
            404);

    private static Task UiPluginErrorAsync(HttpListenerContext context, string message) =>
        HttpHelper.WriteJsonAsync(
            context,
            new { ok = false, error = message, code = "plugin_error" },
            500);
}
