using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.App.Commands;
using NexusPipeline.App.Contracts;
using NexusPipeline.App.Queries;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;

namespace NexusPipeline.Web;

/// <summary>全局用户实体与脚本绑定 API。</summary>
[ApiRoute("users")]
internal static class ApiUsersHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        if (method == "GET" && seg.Length == 1)
        {
            await WriteUsersAsync(context).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 1)
        {
            await CreateUserAsync(context, body).ConfigureAwait(false);
            return;
        }
        if (method == "PUT" && seg.Length == 2 && seg[1].Equals("order", StringComparison.OrdinalIgnoreCase))
        {
            await ReorderUsersAsync(context, body).ConfigureAwait(false);
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
            await GetUserAsync(context, userId).ConfigureAwait(false);
            return;
        }
        if (method == "PUT" && seg.Length == 2)
        {
            await UpdateUserAsync(context, userId, body).ConfigureAwait(false);
            return;
        }
        if (method == "DELETE" && seg.Length == 2)
        {
            await DeleteUserAsync(context, userId, body).ConfigureAwait(false);
            return;
        }
        if (seg.Length >= 3 && seg[2].Equals("avatar", StringComparison.OrdinalIgnoreCase))
        {
            await HandleAvatarAsync(context, method, userId, body).ConfigureAwait(false);
            return;
        }
        if (seg.Length == 3 && seg[2].Equals("global-settings", StringComparison.OrdinalIgnoreCase))
        {
            await HandleGlobalSettingsAsync(context, method, userId, body).ConfigureAwait(false);
            return;
        }
        if (seg.Length >= 3 && seg[2].Equals("bindings", StringComparison.OrdinalIgnoreCase))
        {
            await HandleBindingsAsync(context, method, userId, seg, body).ConfigureAwait(false);
            return;
        }
        await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    private static async Task WriteUsersAsync(HttpListenerContext context)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        IReadOnlyList<UserReadModel> users = ctx.Resolve<UserQueries>().List();
        Audit.Log(Audit.Web, "查询全局用户列表", $"{users.Count} 个");
        await HttpHelper.WriteJsonAsync(context, users.Select(ProjectUser)).ConfigureAwait(false);
    }

    private static async Task GetUserAsync(HttpListenerContext context, string userId)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        UserReadModel? user = ctx.Resolve<UserQueries>().Find(userId);
        if (user is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, ProjectUser(user)).ConfigureAwait(false);
    }

    private static async Task CreateUserAsync(HttpListenerContext context, string body)
    {
        UserPayload? payload = HttpHelper.ParseBody<UserPayload>(body);
        if (payload is null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "用户名不能为空且不能包含非法字符" }, 400).ConfigureAwait(false);
            return;
        }
        RuntimeContext ctx = RuntimeContext.Instance;
        OperationResult<NexusUser> result = UserCommands.Create(
            payload.Name,
            payload.Remark);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        UserReadModel? user = ctx.Resolve<UserQueries>().Find(result.Value!.Id);
        await HttpHelper.WriteJsonAsync(context, ProjectUser(user!)).ConfigureAwait(false);
    }

    private static async Task UpdateUserAsync(HttpListenerContext context, string userId, string body)
    {
        UserPayload? payload = HttpHelper.ParseBody<UserPayload>(body);
        if (payload is null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "用户名不能为空且不能包含非法字符" }, 400).ConfigureAwait(false);
            return;
        }
        RuntimeContext ctx = RuntimeContext.Instance;
        OperationResult<NexusUser> result = UserCommands.Update(
            userId,
            payload.Name,
            payload.Remark);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        UserReadModel? user = ctx.Resolve<UserQueries>().Find(result.Value!.Id);
        await HttpHelper.WriteJsonAsync(context, ProjectUser(user!)).ConfigureAwait(false);
    }

    private static async Task DeleteUserAsync(HttpListenerContext context, string userId, string body)
    {
        JsonNode? node = HttpHelper.ParseBody(body);
        string confirmName = node? ["confirmName"]?.ToString() ?? "";
        OperationResult<bool> result = UserCommands.Delete(userId, confirmName);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }

    private static async Task ReorderUsersAsync(HttpListenerContext context, string body)
    {
        JsonNode? node = HttpHelper.ParseBody(body);
        List<string>? ids = node?["ids"] is JsonArray array
            ? array.Select(item => item?.ToString() ?? "").ToList()
            : null;
        OperationResult<bool> result = UserCommands.Reorder(ids);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }

    private static async Task HandleBindingsAsync(HttpListenerContext context, string method, string userId, string[] seg, string body)
    {
        if (method == "POST" && seg.Length == 3)
        {
            await AddBindingAsync(context, userId, body).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && seg.Length == 3)
        {
            RuntimeContext ctx = RuntimeContext.Instance;
            IReadOnlyList<UserBindingReadModel>? bindings = ctx.Resolve<UserQueries>().ListBindings(userId);
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
            await ReorderBindingsAsync(context, userId, body).ConfigureAwait(false);
            return;
        }
        if (seg.Length == 4 && method == "PUT")
        {
            await UpdateBindingAsync(context, userId, Uri.UnescapeDataString(seg[3]), body).ConfigureAwait(false);
            return;
        }
        if (seg.Length == 4 && method == "DELETE")
        {
            await DeleteBindingAsync(context, userId, Uri.UnescapeDataString(seg[3])).ConfigureAwait(false);
            return;
        }
        if (seg.Length == 5 && seg[4].Equals("edit-config", StringComparison.OrdinalIgnoreCase))
        {
            if (!HttpHelper.IsLoopback(context))
            {
                await HttpHelper.WriteJsonAsync(
                    context,
                    new { ok = false, code = "local_only", error = "编辑配置仅支持本机请求" },
                    403).ConfigureAwait(false);
                return;
            }
            if (method == "GET")
            {
                // v0.12.8：编辑配置前置状态——该用户在脚本实例上是否已有配置快照（首次编辑需选择配置方式）。
                bool hasSnapshot = UserConfigManager.HasSnapshot(
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
                    body).ConfigureAwait(false);
                return;
            }
        }
        await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    private static async Task ReorderBindingsAsync(HttpListenerContext context, string userId, string body)
    {
        JsonNode? node = HttpHelper.ParseBody(body);
        List<string>? ids = node?["ids"] is JsonArray array
            ? array.Select(item => item?.ToString() ?? "").ToList()
            : null;
        OperationResult<bool> result = UserCommands.ReorderBindings(userId, ids);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }

    private static async Task AddBindingAsync(HttpListenerContext context, string userId, string body)
    {
        BindingPayload? payload = HttpHelper.ParseBody<BindingPayload>(body);
        if (payload is null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "必须指定脚本实例" }, 400).ConfigureAwait(false);
            return;
        }
        RuntimeContext ctx = RuntimeContext.Instance;
        OperationResult<UserScriptBinding> result = UserCommands.AddBinding(userId, payload.ToBinding());
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        UserReadModel? user = ctx.Resolve<UserQueries>().Find(userId);
        UserBindingReadModel? binding = user?.Bindings.FirstOrDefault(item =>
            string.Equals(item.ScriptInstanceId, result.Value!.ScriptInstanceId, StringComparison.Ordinal));
        await HttpHelper.WriteJsonAsync(context, binding!).ConfigureAwait(false);
    }

    private static async Task UpdateBindingAsync(HttpListenerContext context, string userId, string scriptId, string body)
    {
        BindingPayload? payload = HttpHelper.ParseBody<BindingPayload>(body);
        if (payload is null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "绑定设置格式不正确" }, 400).ConfigureAwait(false);
            return;
        }
        RuntimeContext ctx = RuntimeContext.Instance;
        OperationResult<UserScriptBinding> result = UserCommands.UpdateBinding(
            userId,
            scriptId,
            payload.ToBinding());
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        UserReadModel? user = ctx.Resolve<UserQueries>().Find(userId);
        UserBindingReadModel? binding = user?.Bindings.FirstOrDefault(item =>
            string.Equals(item.ScriptInstanceId, result.Value!.ScriptInstanceId, StringComparison.Ordinal));
        await HttpHelper.WriteJsonAsync(context, binding!).ConfigureAwait(false);
    }

    private static async Task DeleteBindingAsync(HttpListenerContext context, string userId, string scriptId)
    {
        OperationResult<bool> result = UserCommands.DeleteBinding(userId, scriptId);
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
        string body)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        UserQueries queries = ctx.Resolve<UserQueries>();
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
            await HttpHelper.WriteJsonAsync(context, new { error = "全局设置格式不正确" }, 400).ConfigureAwait(false);
            return;
        }
        OperationResult<UserBindingOverrides> result = UserCommands.UpdateGlobalSettings(userId, payload);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, result.Value ?? new UserBindingOverrides()).ConfigureAwait(false);
    }

    private static async Task HandleAvatarAsync(HttpListenerContext context, string method, string userId, string body)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        if (ctx.Resolve<UserQueries>().Find(userId) is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        if (method == "GET")
        {
            await GetAvatarAsync(context, userId).ConfigureAwait(false);
            return;
        }
        if (method == "DELETE")
        {
            OperationResult<bool> result = UserCommands.RemoveAvatar(userId);
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
            await SaveAvatarAsync(context, userId, body).ConfigureAwait(false);
            return;
        }
        await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    private static async Task SaveAvatarAsync(HttpListenerContext context, string userId, string body)
    {
        AvatarPayload? payload = HttpHelper.ParseBody<AvatarPayload>(body);
        string mime = payload?.MimeType?.Trim().ToLowerInvariant() ?? "";
        string extension = mime switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpg",
            "image/webp" => "webp",
            _ => "",
        };
        if (extension.Length == 0 || string.IsNullOrWhiteSpace(payload?.Data))
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "头像仅支持 PNG、JPEG 或 WebP" }, 400).ConfigureAwait(false);
            return;
        }
        byte[] data;
        try
        {
            data = Convert.FromBase64String(payload!.Data);
        }
        catch
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "头像数据不是有效的 Base64" }, 400).ConfigureAwait(false);
            return;
        }
        OperationResult<bool> result = UserCommands.SetAvatar(userId, mime, data);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { ok = true, avatarUrl = $"/api/users/{Uri.EscapeDataString(userId)}/avatar" }).ConfigureAwait(false);
    }

    private static async Task GetAvatarAsync(HttpListenerContext context, string userId)
    {
        string dir = Path.Combine(AppPaths.UserAssetsDir, userId);
        string? file = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "avatar.*").FirstOrDefault(path =>
                Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase))
            : null;
        if (file is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        string contentType = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            _ => "image/webp",
        };
        byte[] data = File.ReadAllBytes(file);
        context.Response.StatusCode = 200;
        context.Response.ContentType = contentType;
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.ContentLength64 = data.Length;
        await context.Response.OutputStream.WriteAsync(data).ConfigureAwait(false);
        context.Response.OutputStream.Close();
    }

    private static object ProjectUser(UserReadModel user)
    {
        return new
        {
            user.Id,
            user.Index,
            user.Name,
            user.Remark,
            avatarUrl = HasAvatar(user.Id) ? $"/api/users/{Uri.EscapeDataString(user.Id)}/avatar" : null,
            bindingCount = user.BindingCount,
            nextRunAt = user.NextRunAt,
            nextQueueName = user.NextQueueName,
            bindings = user.Bindings,
        };
    }

    private static bool HasAvatar(string userId)
    {
        string dir = Path.Combine(AppPaths.UserAssetsDir, userId);
        return Directory.Exists(dir) && Directory.GetFiles(dir, "avatar.*").Any();
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

        public UserScriptBinding ToBinding()
        {
            return new UserScriptBinding
            {
                ScriptInstanceId = ScriptInstanceId.Trim(),
                Enabled = Enabled,
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
