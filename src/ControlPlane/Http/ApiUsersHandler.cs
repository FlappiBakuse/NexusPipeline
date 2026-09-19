using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users.Queries;
using NexusPipeline.Modules.Users.UseCases;
using NexusPipeline.Modules.Users;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Results;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Modules.Configuration.Editing;

namespace NexusPipeline.ControlPlane.Http;

/// <summary>全局用户实体与脚本绑定 API。</summary>
[ApiRoute("users")]
internal static class ApiUsersHandler
{
    public static async Task Handle(
        HttpListenerContext context,
        string method,
        string[] seg,
        string body,
        UserCommands userCommands,
        ConfigEditCommands configEditCommands,
        UserQueries userQueries,
        UserAssetService userAssets)
    {
        if (method == "GET" && seg.Length == 1)
        {
            await WriteUsersAsync(context, userQueries, userAssets).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 1)
        {
            await CreateUserAsync(context, body, userCommands, userQueries, userAssets).ConfigureAwait(false);
            return;
        }
        if (method == "PUT" && seg.Length == 2 && seg[1].Equals("order", StringComparison.OrdinalIgnoreCase))
        {
            await ReorderUsersAsync(context, body, userCommands).ConfigureAwait(false);
            return;
        }
        if (seg.Length < 2)
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }

        string userId = Uri.UnescapeDataString(seg[1]);
        if (method == "GET" && seg.Length == 2)
        {
            await GetUserAsync(context, userId, userQueries, userAssets).ConfigureAwait(false);
            return;
        }
        if (method == "PUT" && seg.Length == 2)
        {
            await UpdateUserAsync(context, userId, body, userCommands, userQueries, userAssets).ConfigureAwait(false);
            return;
        }
        if (method == "DELETE" && seg.Length == 2)
        {
            await DeleteUserAsync(context, userId, body, userCommands).ConfigureAwait(false);
            return;
        }
        if (seg.Length >= 3 && seg[2].Equals("avatar", StringComparison.OrdinalIgnoreCase))
        {
            await HandleAvatarAsync(context, method, userId, body, userCommands, userQueries, userAssets).ConfigureAwait(false);
            return;
        }
        if (seg.Length == 3 && seg[2].Equals("global-settings", StringComparison.OrdinalIgnoreCase))
        {
            await HandleGlobalSettingsAsync(context, method, userId, body, userCommands, userQueries).ConfigureAwait(false);
            return;
        }
        if (seg.Length >= 3 && seg[2].Equals("bindings", StringComparison.OrdinalIgnoreCase))
        {
            await HandleBindingsAsync(context, method, userId, seg, body, userCommands, configEditCommands, userQueries).ConfigureAwait(false);
            return;
        }
        await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    private static async Task WriteUsersAsync(HttpListenerContext context, UserQueries queries, UserAssetService userAssets)
    {
        IReadOnlyList<UserReadModel> users = queries.List();
        Audit.Log(Audit.Web, "查询全局用户列表", $"{users.Count} 个");
        await HttpHelper.WriteJsonAsync(context, users.Select(user => ProjectUser(user, userAssets))).ConfigureAwait(false);
    }

    private static async Task GetUserAsync(HttpListenerContext context, string userId, UserQueries queries, UserAssetService userAssets)
    {
        UserReadModel? user = queries.Find(userId);
        if (user is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, ProjectUser(user, userAssets)).ConfigureAwait(false);
    }

    private static async Task CreateUserAsync(
        HttpListenerContext context,
        string body,
        UserCommands userCommands,
        UserQueries queries,
        UserAssetService userAssets)
    {
        UserPayload? payload = HttpHelper.ParseBody<UserPayload>(body);
        if (payload is null)
        {
            await HttpHelper.ErrorAsync(context, "user_name_invalid", 400).ConfigureAwait(false);
            return;
        }
        OperationResult<NexusUser> result = userCommands.Create(
            payload.Name,
            payload.Remark);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        UserReadModel? user = queries.Find(result.Value!.Id);
        await HttpHelper.WriteJsonAsync(context, ProjectUser(user!, userAssets)).ConfigureAwait(false);
    }

    private static async Task UpdateUserAsync(
        HttpListenerContext context,
        string userId,
        string body,
        UserCommands userCommands,
        UserQueries queries,
        UserAssetService userAssets)
    {
        UserPayload? payload = HttpHelper.ParseBody<UserPayload>(body);
        if (payload is null)
        {
            await HttpHelper.ErrorAsync(context, "user_name_invalid", 400).ConfigureAwait(false);
            return;
        }
        OperationResult<NexusUser> result = userCommands.Update(
            userId,
            payload.Name,
            payload.Remark);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        UserReadModel? user = queries.Find(result.Value!.Id);
        await HttpHelper.WriteJsonAsync(context, ProjectUser(user!, userAssets)).ConfigureAwait(false);
    }

    private static async Task DeleteUserAsync(HttpListenerContext context, string userId, string body, UserCommands userCommands)
    {
        JsonNode? node = HttpHelper.ParseBody(body);
        string confirmName = node? ["confirmName"]?.ToString() ?? "";
        OperationResult<bool> result = userCommands.Delete(userId, confirmName);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }

    private static async Task ReorderUsersAsync(HttpListenerContext context, string body, UserCommands userCommands)
    {
        JsonNode? node = HttpHelper.ParseBody(body);
        List<string>? ids = node?["ids"] is JsonArray array
            ? array.Select(item => item?.ToString() ?? "").ToList()
            : null;
        OperationResult<bool> result = userCommands.Reorder(ids);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }

    private static async Task HandleBindingsAsync(
        HttpListenerContext context,
        string method,
        string userId,
        string[] seg,
        string body,
        UserCommands userCommands,
        ConfigEditCommands configEditCommands,
        UserQueries queries)
    {
        if (method == "POST" && seg.Length == 3)
        {
            await AddBindingAsync(context, userId, body, userCommands, queries).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && seg.Length == 3)
        {
            IReadOnlyList<UserBindingReadModel>? bindings = queries.ListBindings(userId);
            if (bindings is null)
            {
                await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(context, bindings).ConfigureAwait(false);
            return;
        }
        if (method == "PUT" && seg.Length == 4 && seg[3].Equals("order", StringComparison.OrdinalIgnoreCase))
        {
            await ReorderBindingsAsync(context, userId, body, userCommands).ConfigureAwait(false);
            return;
        }
        if (seg.Length == 4 && method == "PUT")
        {
            await UpdateBindingAsync(context, userId, Uri.UnescapeDataString(seg[3]), body, userCommands, queries).ConfigureAwait(false);
            return;
        }
        if (seg.Length == 4 && method == "DELETE")
        {
            await DeleteBindingAsync(context, userId, Uri.UnescapeDataString(seg[3]), userCommands).ConfigureAwait(false);
            return;
        }
        if (seg.Length == 5 && seg[4].Equals("edit-config", StringComparison.OrdinalIgnoreCase))
        {
            if (!HttpHelper.IsLoopback(context))
            {
                await HttpHelper.ErrorAsync(context, "local_only", 403).ConfigureAwait(false);
                return;
            }
            if (method == "GET")
            {
                // v0.12.8：编辑配置前置状态——该用户在脚本实例上是否已有配置快照（首次编辑需选择配置方式）。
                bool hasSnapshot = ConfigSnapshotService.HasSnapshot(
                    Uri.UnescapeDataString(seg[3]),
                    userId);
                await HttpHelper.WriteJsonAsync(context, new { hasSnapshot }).ConfigureAwait(false);
                return;
            }
            if (method == "POST")
            {
                await ConfigEditHttpAdapter.HandleByUserIdAsync(
                    context,
                    Uri.UnescapeDataString(seg[3]),
                    userId,
                    body,
                    configEditCommands).ConfigureAwait(false);
                return;
            }
        }
        await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    private static async Task ReorderBindingsAsync(HttpListenerContext context, string userId, string body, UserCommands userCommands)
    {
        JsonNode? node = HttpHelper.ParseBody(body);
        List<string>? ids = node?["ids"] is JsonArray array
            ? array.Select(item => item?.ToString() ?? "").ToList()
            : null;
        OperationResult<bool> result = userCommands.ReorderBindings(userId, ids);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }

    private static async Task AddBindingAsync(
        HttpListenerContext context,
        string userId,
        string body,
        UserCommands userCommands,
        UserQueries queries)
    {
        BindingPayload? payload = HttpHelper.ParseBody<BindingPayload>(body);
        if (payload is null)
        {
            await HttpHelper.ErrorAsync(context, "script_instance_required", 400).ConfigureAwait(false);
            return;
        }
        OperationResult<UserScriptBinding> result = userCommands.AddBinding(userId, payload.ToBinding());
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        UserReadModel? user = queries.Find(userId);
        UserBindingReadModel? binding = user?.Bindings.FirstOrDefault(item =>
            string.Equals(item.ScriptInstanceId, result.Value!.ScriptInstanceId, StringComparison.Ordinal));
        await HttpHelper.WriteJsonAsync(context, binding!).ConfigureAwait(false);
    }

    private static async Task UpdateBindingAsync(
        HttpListenerContext context,
        string userId,
        string scriptId,
        string body,
        UserCommands userCommands,
        UserQueries queries)
    {
        BindingPayload? payload = HttpHelper.ParseBody<BindingPayload>(body);
        if (payload is null)
        {
            await HttpHelper.ErrorAsync(context, "binding_invalid", 400).ConfigureAwait(false);
            return;
        }
        OperationResult<UserScriptBinding> result = userCommands.UpdateBinding(
            userId,
            scriptId,
            new UserBindingUpdateRequest(
                payload.ToBinding(),
                ConfigInputsSpecified: payload.ConfigInputs is not null));
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        UserReadModel? user = queries.Find(userId);
        UserBindingReadModel? binding = user?.Bindings.FirstOrDefault(item =>
            string.Equals(item.ScriptInstanceId, result.Value!.ScriptInstanceId, StringComparison.Ordinal));
        await HttpHelper.WriteJsonAsync(context, binding!).ConfigureAwait(false);
    }

    private static async Task DeleteBindingAsync(HttpListenerContext context, string userId, string scriptId, UserCommands userCommands)
    {
        OperationResult<bool> result = userCommands.DeleteBinding(userId, scriptId);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }

    private static async Task HandleGlobalSettingsAsync(
        HttpListenerContext context,
        string method,
        string userId,
        string body,
        UserCommands userCommands,
        UserQueries queries)
    {
        UserReadModel? user = queries.Find(userId);
        if (user is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        if (method == "GET")
        {
            await HttpHelper.WriteJsonAsync(
                context,
                queries.FindGlobalSettings(userId) ?? new UserBindingOverrides()).ConfigureAwait(false);
            return;
        }
        if (method != "PUT")
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        UserBindingOverrides? payload = HttpHelper.ParseBody<UserBindingOverrides>(body);
        if (payload is null)
        {
            await HttpHelper.ErrorAsync(context, "global_settings_invalid", 400).ConfigureAwait(false);
            return;
        }
        OperationResult<UserBindingOverrides> result = userCommands.UpdateGlobalSettings(userId, payload);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, result.Value ?? new UserBindingOverrides()).ConfigureAwait(false);
    }

    private static async Task HandleAvatarAsync(
        HttpListenerContext context,
        string method,
        string userId,
        string body,
        UserCommands userCommands,
        UserQueries queries,
        UserAssetService userAssets)
    {
        if (queries.Find(userId) is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        if (method == "GET")
        {
            await GetAvatarAsync(context, userId, userAssets).ConfigureAwait(false);
            return;
        }
        if (method == "DELETE")
        {
            OperationResult<bool> result = userCommands.RemoveAvatar(userId);
            if (!result.Succeeded)
            {
                await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
            return;
        }
        if (method == "POST")
        {
            await SaveAvatarAsync(context, userId, body, userCommands).ConfigureAwait(false);
            return;
        }
        await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    private static async Task SaveAvatarAsync(HttpListenerContext context, string userId, string body, UserCommands userCommands)
    {
        AvatarPayload? payload = HttpHelper.ParseBody<AvatarPayload>(body);
        if (!TryDecodeAvatar(
                payload?.MimeType,
                payload?.Data,
                out string mime,
                out byte[] data,
                out string errorCode))
        {
            await HttpHelper.ErrorAsync(context, errorCode, 400).ConfigureAwait(false);
            return;
        }
        OperationResult<bool> result = userCommands.SetAvatar(userId, mime, data);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { ok = true, avatarUrl = $"/api/users/{Uri.EscapeDataString(userId)}/avatar" }).ConfigureAwait(false);
    }

    internal static bool TryDecodeAvatar(
        string? mimeType,
        string? encodedData,
        out string normalizedMimeType,
        out byte[] data,
        out string errorCode)
    {
        normalizedMimeType = mimeType?.Trim().ToLowerInvariant() ?? "";
        data = Array.Empty<byte>();
        errorCode = "";
        if (normalizedMimeType is not ("image/png" or "image/jpeg" or "image/webp")
            || string.IsNullOrWhiteSpace(encodedData))
        {
            errorCode = "avatar_type_invalid";
            return false;
        }
        try
        {
            data = Convert.FromBase64String(encodedData);
            return true;
        }
        catch (FormatException)
        {
            errorCode = "avatar_data_invalid";
            return false;
        }
    }

    private static async Task GetAvatarAsync(
        HttpListenerContext context,
        string userId,
        UserAssetService userAssets)
    {
        UserAvatarContent? avatar = userAssets.ReadAvatar(userId);
        if (avatar is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        context.Response.StatusCode = 200;
        context.Response.ContentType = avatar.ContentType;
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.ContentLength64 = avatar.Data.Length;
        await context.Response.OutputStream.WriteAsync(avatar.Data).ConfigureAwait(false);
        context.Response.OutputStream.Close();
    }

    private static object ProjectUser(UserReadModel user, UserAssetService userAssets)
    {
        return new
        {
            user.Id,
            user.Index,
            user.Name,
            user.Remark,
            avatarUrl = userAssets.HasAvatar(user.Id) ? $"/api/users/{Uri.EscapeDataString(user.Id)}/avatar" : null,
            bindingCount = user.BindingCount,
            nextRunAt = user.NextRunAt,
            nextQueueName = user.NextQueueName,
            bindings = user.Bindings,
        };
    }

    private sealed class UserPayload
    {
        public string Name { get; set; } = "";

        public string? Remark { get; set; }
    }

    private sealed class BindingPayload
    {
        public string ScriptInstanceId { get; set; } = "";

        public bool Enabled { get; set; } = true;

        public string PreRunScript { get; set; } = "";

        public bool PreRunOnceOnly { get; set; }

        public string PostRunScript { get; set; } = "";

        public bool PostRunOnFinalOnly { get; set; }

        public bool NotifyEnabled { get; set; } = true;

        public string SmtpTo { get; set; } = "";

        public int RunDays { get; set; } = -1;

        public int MaxSuccessfulRunsPerDay { get; set; } = -1;

        /// <summary>用户级专项插件输入值（如 BetterGI 一条龙配置名、ZZZ 实例序号）；解析时优先于脚本实例的 pluginInputs。</summary>
        public Dictionary<string, string>? ConfigInputs { get; set; }

        public UserScriptBinding ToBinding()
        {
            return new UserScriptBinding
            {
                ScriptInstanceId = ScriptInstanceId.Trim(),
                Enabled = Enabled,
                ConfigInputs = new Dictionary<string, string>(ConfigInputs ?? new(), StringComparer.OrdinalIgnoreCase),
                PreRunScript = PreRunScript.Trim(),
                PreRunOnceOnly = PreRunOnceOnly,
                PostRunScript = PostRunScript.Trim(),
                PostRunOnFinalOnly = PostRunOnFinalOnly,
                NotifyEnabled = NotifyEnabled,
                SmtpTo = SmtpTo.Trim(),
                RunDays = RunDays,
                MaxSuccessfulRunsPerDay = MaxSuccessfulRunsPerDay,
            };
        }
    }

    private sealed class AvatarPayload
    {
        public string MimeType { get; set; } = "";

        public string Data { get; set; } = "";
    }
}
