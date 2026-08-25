using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.Web;

/// <summary>全局用户实体与脚本绑定 API（v0.9.6）。</summary>
[ApiRoute("users")]
internal static class ApiUsersHandler
{
    private const int MaxAvatarBytes = 5 * 1024 * 1024;

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
        List<NexusUser> users = ctx.SnapshotUsers().OrderBy(user => user.Index).ToList();
        List<ScriptInstance> scripts = ctx.SnapshotScripts();
        List<DispatchQueue> queues = ctx.SnapshotQueues();
        Audit.Log(Audit.Web, "查询全局用户列表", $"{users.Count} 个");
        await HttpHelper.WriteJsonAsync(context, users.Select(user => ProjectUser(user, scripts, queues))).ConfigureAwait(false);
    }

    private static async Task GetUserAsync(HttpListenerContext context, string userId)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        NexusUser? user = ctx.FindUser(userId);
        if (user is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(
            context,
            ProjectUser(user, ctx.SnapshotScripts(), ctx.SnapshotQueues())).ConfigureAwait(false);
    }

    private static async Task CreateUserAsync(HttpListenerContext context, string body)
    {
        UserPayload? payload = HttpHelper.ParseBody<UserPayload>(body);
        string? validation = ValidateName(payload?.Name);
        if (validation is not null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = validation }, 400).ConfigureAwait(false);
            return;
        }
        string? remarkError = ValidateRemark(payload?.Remark);
        if (remarkError is not null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = remarkError }, 400).ConfigureAwait(false);
            return;
        }
        if (payload!.AutoCheckInEnabled)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "自动签到将在后续版本通过插件实现" }, 400).ConfigureAwait(false);
            return;
        }

        RuntimeContext ctx = RuntimeContext.Instance;
        NexusUser? created = null;
        string? error = null;
        ctx.Center.WithAdmissionCoordination(() =>
        {
            lock (ctx.DataLock)
            {
                error = Limits.CheckGlobalUserCount(ctx.Users.Count)
                    ?? (ctx.Users.Any(user => string.Equals(user.Name, payload.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                        ? "用户名重复：全局用户已存在同名用户"
                        : null);
                if (error is not null)
                {
                    return;
                }
                created = new NexusUser
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Index = ctx.Users.Count == 0 ? 0 : ctx.Users.Max(user => user.Index) + 1,
                    Name = payload.Name.Trim(),
                    Remark = payload.Remark?.Trim() ?? "",
                    AutoCheckInEnabled = false,
                };
                ctx.Users.Add(created);
                try
                {
                    DataStore.SaveUsers(ctx.Users);
                }
                catch
                {
                    ctx.Users.Remove(created);
                    throw;
                }
            }
        });
        if (error is not null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error }, 400).ConfigureAwait(false);
            return;
        }
        Audit.Log(Audit.Web, "添加全局用户", $"{created!.Name}（id={created.Id}）");
        await HttpHelper.WriteJsonAsync(
            context,
            ProjectUser(created, ctx.SnapshotScripts(), ctx.SnapshotQueues())).ConfigureAwait(false);
    }

    private static async Task UpdateUserAsync(HttpListenerContext context, string userId, string body)
    {
        UserPayload? payload = HttpHelper.ParseBody<UserPayload>(body);
        string? validation = ValidateName(payload?.Name);
        if (validation is not null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = validation }, 400).ConfigureAwait(false);
            return;
        }
        if (payload!.AutoCheckInEnabled)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "自动签到将在后续版本通过插件实现" }, 400).ConfigureAwait(false);
            return;
        }
        string? remarkError = ValidateRemark(payload?.Remark);
        if (remarkError is not null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = remarkError }, 400).ConfigureAwait(false);
            return;
        }

        RuntimeContext ctx = RuntimeContext.Instance;
        NexusUser? target = ctx.FindUser(userId);
        if (target is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        UserPayload data = payload!;
        string? error = null;
        ctx.Center.WithAdmissionCoordination(() =>
        {
            error = CheckUserMutationBusy(ctx, target);
            if (error is not null)
            {
                return;
            }
            lock (ctx.DataLock)
            {
                if (ctx.Users.Any(user => !ReferenceEquals(user, target)
                    && string.Equals(user.Name, data.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    error = "用户名重复：全局用户已存在同名用户";
                    return;
                }
                string oldName = target.Name;
                bool oldAuto = target.AutoCheckInEnabled;
                string oldRemark = target.Remark;
                target.Name = data.Name.Trim();
                target.AutoCheckInEnabled = false;
                target.Remark = data.Remark?.Trim() ?? "";
                try
                {
                    DataStore.SaveUsers(ctx.Users);
                }
                catch
                {
                    target.Name = oldName;
                    target.AutoCheckInEnabled = oldAuto;
                    target.Remark = oldRemark;
                    throw;
                }
            }
        });
        if (error is not null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error }, error.Contains("运行", StringComparison.Ordinal) || error.Contains("待执行", StringComparison.Ordinal) ? 409 : 400).ConfigureAwait(false);
            return;
        }
        Audit.Log(Audit.Web, "编辑全局用户", $"{userId} → {target.Name}");
        await HttpHelper.WriteJsonAsync(
            context,
            ProjectUser(target, ctx.SnapshotScripts(), ctx.SnapshotQueues())).ConfigureAwait(false);
    }

    private static async Task DeleteUserAsync(HttpListenerContext context, string userId, string body)
    {
        JsonNode? node = HttpHelper.ParseBody(body);
        string confirmName = node? ["confirmName"]?.ToString() ?? "";
        RuntimeContext ctx = RuntimeContext.Instance;
        NexusUser? target = ctx.FindUser(userId);
        if (target is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        if (!string.Equals(confirmName, target.Name, StringComparison.Ordinal))
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "请完整输入用户名以确认删除" }, 400).ConfigureAwait(false);
            return;
        }

        List<UserScriptBinding> bindings = target.Bindings.Select(binding => binding.Clone()).ToList();
        string? error = null;
        ctx.Center.WithAdmissionCoordination(() =>
        {
            error = CheckUserMutationBusy(ctx, target);
            if (error is not null)
            {
                return;
            }
            lock (ctx.DataLock)
            {
                int index = ctx.Users.IndexOf(target);
                if (index < 0)
                {
                    error = "用户不存在";
                    return;
                }
                ctx.Users.RemoveAt(index);
                try
                {
                    // 先提交元数据，配置目录删除失败仍可由管理员从原目录取回。
                    DataStore.SaveUsers(ctx.Users);
                }
                catch
                {
                    ctx.Users.Insert(index, target);
                    throw;
                }
            }
            foreach (UserScriptBinding binding in bindings)
            {
                UserConfigManager.RemoveUserData(binding.ScriptInstanceId, target.Id);
            }
            DeleteAvatarFiles(target.Id);
        });
        if (error is not null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error }, error.Contains("运行", StringComparison.Ordinal) || error.Contains("待执行", StringComparison.Ordinal) ? 409 : 400).ConfigureAwait(false);
            return;
        }
        Audit.Log(Audit.Web, "删除全局用户", $"{target.Name}（id={target.Id}）");
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }

    private static async Task ReorderUsersAsync(HttpListenerContext context, string body)
    {
        JsonNode? node = HttpHelper.ParseBody(body);
        List<string>? ids = node?["ids"] is JsonArray array
            ? array.Select(item => item?.ToString() ?? "").ToList()
            : null;
        RuntimeContext ctx = RuntimeContext.Instance;
        string? error = null;
        ctx.Center.WithAdmissionCoordination(() =>
        {
            lock (ctx.DataLock)
            {
                if (ids is null || ids.Count != ctx.Users.Count
                    || ids.Any(string.IsNullOrWhiteSpace)
                    || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Count)
                {
                    error = "用户顺序名单缺失或与当前全局用户列表不一致";
                    return;
                }
                HashSet<string> existing = new(ctx.Users.Select(user => user.Id), StringComparer.OrdinalIgnoreCase);
                if (ids.Any(id => !existing.Contains(id)))
                {
                    error = "用户顺序名单与当前全局用户列表不一致";
                    return;
                }
                Dictionary<string, NexusUser> byId = ctx.Users.ToDictionary(user => user.Id, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < ids.Count; i++)
                {
                    byId[ids[i]].Index = i;
                }
                DataStore.SaveUsers(ctx.Users);
            }
        });
        if (error is not null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error }, 400).ConfigureAwait(false);
            return;
        }
        ctx.Scheduler.RevalidatePendingPlans();
        Audit.Log(Audit.Web, "调整全局用户顺序", $"{ids!.Count} 个用户");
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
            NexusUser? user = ctx.FindUser(userId);
            if (user is null)
            {
                await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                return;
            }
            List<ScriptInstance> scripts = ctx.SnapshotScripts();
            await HttpHelper.WriteJsonAsync(context, user.Bindings.Select(binding => ProjectBinding(binding, scripts))).ConfigureAwait(false);
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
        if (seg.Length == 5 && method == "POST" && seg[4].Equals("edit-config", StringComparison.OrdinalIgnoreCase))
        {
            await ApiScriptsHandler.HandleEditConfigByUserIdAsync(
                context,
                Uri.UnescapeDataString(seg[3]),
                userId,
                body).ConfigureAwait(false);
            return;
        }
        await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    private static async Task AddBindingAsync(HttpListenerContext context, string userId, string body)
    {
        BindingPayload? payload = HttpHelper.ParseBody<BindingPayload>(body);
        if (payload is null || string.IsNullOrWhiteSpace(payload.ScriptInstanceId))
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "必须指定脚本实例" }, 400).ConfigureAwait(false);
            return;
        }
        string? validation = ValidateSmtp(payload.SmtpTo);
        if (validation is not null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = validation }, 400).ConfigureAwait(false);
            return;
        }
        string? runDaysError = ValidateRunDays(payload.RunDays);
        if (runDaysError is not null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = runDaysError }, 400).ConfigureAwait(false);
            return;
        }
        RuntimeContext ctx = RuntimeContext.Instance;
        NexusUser? user = ctx.FindUser(userId);
        ScriptInstance? script = ctx.FindScript(payload.ScriptInstanceId);
        if (user is null || script is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        UserScriptBinding binding = payload.ToBinding();
        string? error = null;
        SemaphoreSlim gate = ScriptConfigGate.Get(script.Id);
        bool gateHeld = false;
        ctx.Center.WithAdmissionCoordination(() =>
        {
            try
            {
                if (ctx.Center.FindLeases(script.Id).Count > 0)
                {
                    error = "脚本正在运行，无法新增绑定";
                    return;
                }
                if (UserConfigManager.EditSessions.Values.Any(session => session.Script.Id == script.Id))
                {
                    error = "脚本正在编辑配置中，无法新增绑定";
                    return;
                }
                if (!gate.Wait(0))
                {
                    error = "脚本正在运行或编辑配置中，无法新增绑定";
                    return;
                }
                gateHeld = true;

                error = CheckBindingBusy(ctx, user.Id, script.Id);
                if (error is not null)
                {
                    return;
                }

                ScriptInstance snapshotScript;
                lock (ctx.DataLock)
                {
                    if (user.Bindings.Any(item => string.Equals(item.ScriptInstanceId, script.Id, StringComparison.Ordinal)))
                    {
                        error = "该用户已绑定此脚本实例";
                        return;
                    }
                    int current = ctx.Users.Sum(item => item.Bindings.Count(bindingItem =>
                        string.Equals(bindingItem.ScriptInstanceId, script.Id, StringComparison.Ordinal)));
                    error = Limits.CheckUserCount(current);
                    if (error is not null)
                    {
                        return;
                    }
                    snapshotScript = script.Clone();
                }

                string? snapshotError = UserConfigManager.SnapshotOnAddUser(snapshotScript, user.Id);
                if (snapshotError is not null)
                {
                    error = "初始配置快照失败：" + snapshotError;
                    return;
                }

                lock (ctx.DataLock)
                {
                    if (user.Bindings.Any(item => string.Equals(item.ScriptInstanceId, script.Id, StringComparison.Ordinal)))
                    {
                        error = "该用户已绑定此脚本实例";
                        return;
                    }
                    user.Bindings.Add(binding);
                    try
                    {
                        DataStore.SaveUsers(ctx.Users);
                    }
                    catch
                    {
                        user.Bindings.Remove(binding);
                        throw;
                    }
                }
            }
            finally
            {
                if (gateHeld)
                {
                    gate.Release();
                    gateHeld = false;
                }
            }
        });
        if (error is not null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error },
                error.Contains("运行", StringComparison.Ordinal)
                    || error.Contains("编辑", StringComparison.Ordinal)
                    || error.Contains("待执行", StringComparison.Ordinal)
                    ? 409
                    : 400).ConfigureAwait(false);
            return;
        }
        ctx.Scheduler.RevalidatePendingPlans();
        Audit.Log(Audit.Web, "绑定全局用户脚本", $"{user.Name} / {script.Name}");
        await HttpHelper.WriteJsonAsync(context, ProjectBinding(binding, ctx.SnapshotScripts())).ConfigureAwait(false);
    }

    private static async Task UpdateBindingAsync(HttpListenerContext context, string userId, string scriptId, string body)
    {
        BindingPayload? payload = HttpHelper.ParseBody<BindingPayload>(body);
        string? validation = ValidateSmtp(payload?.SmtpTo);
        if (payload is null || validation is not null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = validation ?? "绑定设置格式不正确" }, 400).ConfigureAwait(false);
            return;
        }
        string? runDaysError = ValidateRunDays(payload.RunDays);
        if (runDaysError is not null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = runDaysError }, 400).ConfigureAwait(false);
            return;
        }
        RuntimeContext ctx = RuntimeContext.Instance;
        NexusUser? user = ctx.FindUser(userId);
        if (user is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        UserScriptBinding? existing = user.Bindings.FirstOrDefault(binding => binding.ScriptInstanceId == scriptId);
        if (existing is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        string? error = null;
        ctx.Center.WithAdmissionCoordination(() =>
        {
            error = CheckBindingBusy(ctx, user.Id, scriptId);
            if (error is not null)
            {
                return;
            }
            lock (ctx.DataLock)
            {
                UserScriptBinding old = existing.Clone();
                UserScriptBinding replacement = payload.ToBinding();
                replacement.ScriptInstanceId = scriptId;
                int index = user.Bindings.IndexOf(existing);
                user.Bindings[index] = replacement;
                try
                {
                    DataStore.SaveUsers(ctx.Users);
                }
                catch
                {
                    user.Bindings[index] = old;
                    throw;
                }
                existing = replacement;
            }
        });
        if (error is not null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error }, 409).ConfigureAwait(false);
            return;
        }
        ctx.Scheduler.RevalidatePendingPlans();
        await HttpHelper.WriteJsonAsync(context, ProjectBinding(existing!, ctx.SnapshotScripts())).ConfigureAwait(false);
    }

    private static async Task DeleteBindingAsync(HttpListenerContext context, string userId, string scriptId)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        NexusUser? user = ctx.FindUser(userId);
        UserScriptBinding? binding = user?.Bindings.FirstOrDefault(item => item.ScriptInstanceId == scriptId);
        if (user is null || binding is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        string? error = null;
        ctx.Center.WithAdmissionCoordination(() =>
        {
            error = CheckBindingBusy(ctx, user.Id, scriptId);
            if (error is not null)
            {
                return;
            }
            lock (ctx.DataLock)
            {
                int index = user.Bindings.IndexOf(binding);
                if (index < 0)
                {
                    error = "绑定不存在";
                    return;
                }
                user.Bindings.RemoveAt(index);
                try
                {
                    // 先提交解绑元数据，再清理该绑定的配置快照。
                    DataStore.SaveUsers(ctx.Users);
                    UserConfigManager.RemoveUserData(scriptId, user.Id);
                }
                catch
                {
                    user.Bindings.Insert(index, binding);
                    try { DataStore.SaveUsers(ctx.Users); } catch { }
                    throw;
                }
            }
        });
        if (error is not null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error }, 409).ConfigureAwait(false);
            return;
        }
        ctx.Scheduler.RevalidatePendingPlans();
        Audit.Log(Audit.Web, "解除全局用户脚本绑定", $"{user.Name} / {scriptId}");
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }

    private static async Task HandleAvatarAsync(HttpListenerContext context, string method, string userId, string body)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        if (ctx.FindUser(userId) is null)
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
            DeleteAvatarFiles(userId);
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
        if (data.Length == 0 || data.Length > MaxAvatarBytes || !HasMatchingMagic(mime, data))
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "头像文件格式或大小不符合要求（上限 5 MiB）" }, 400).ConfigureAwait(false);
            return;
        }
        string dir = Path.Combine(AppPaths.UserAssetsDir, userId);
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "avatar." + extension);
        File.WriteAllBytes(target, data);
        foreach (string file in Directory.GetFiles(dir, "avatar.*"))
        {
            if (!string.Equals(file, target, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
            }
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

    private static object ProjectUser(NexusUser user, IReadOnlyList<ScriptInstance> scripts, IReadOnlyList<DispatchQueue> queues)
    {
        (string QueueName, DateTime TriggerTime)? next = RuntimeContext.Instance.Scheduler.NextTriggerForUser(user, queues);
        return new
        {
            user.Id,
            user.Index,
            user.Name,
            user.Remark,
            user.AutoCheckInEnabled,
            avatarUrl = HasAvatar(user.Id) ? $"/api/users/{Uri.EscapeDataString(user.Id)}/avatar" : null,
            bindingCount = user.Bindings.Count,
            nextRunAt = next?.TriggerTime,
            nextQueueName = next?.QueueName,
            bindings = user.Bindings.Select(binding => ProjectBinding(binding, scripts)),
        };
    }

    private static object ProjectBinding(UserScriptBinding binding, IReadOnlyList<ScriptInstance> scripts)
    {
        ScriptInstance? script = scripts.FirstOrDefault(item => item.Id == binding.ScriptInstanceId);
        return new
        {
            binding.ScriptInstanceId,
            scriptName = script?.Name ?? "（脚本实例不存在）",
            binding.Enabled,
            binding.PreRunScript,
            binding.PreRunOnceOnly,
            binding.PostRunScript,
            binding.PostRunOnFinalOnly,
            binding.NotifyEnabled,
            binding.SmtpTo,
            binding.RunDays,
        };
    }

    private static string? CheckUserMutationBusy(RuntimeContext ctx, NexusUser user)
    {
        if (ctx.Scheduler.HasPendingUser(user.Id))
        {
            return "用户已存在待执行的冻结计划，暂时无法修改";
        }
        foreach (UserScriptBinding binding in user.Bindings)
        {
            if (CheckBindingBusy(ctx, user.Id, binding.ScriptInstanceId) is string error)
            {
                return error;
            }
        }
        return null;
    }

    private static string? CheckBindingBusy(RuntimeContext ctx, string userId, string scriptId)
    {
        if (ctx.Center.FindLeases(scriptId, userId).Count > 0)
        {
            return "用户绑定正在运行，无法修改";
        }
        if (UserConfigManager.EditSessions.Values.Any(session =>
            session.Script.Id == scriptId && string.Equals(session.Mark.UserName, userId, StringComparison.OrdinalIgnoreCase)))
        {
            return "用户绑定正在编辑配置，无法修改";
        }
        if (ctx.Scheduler.HasPendingBinding(userId, scriptId))
        {
            return "该用户绑定已存在待执行的冻结计划，暂时无法修改";
        }
        return null;
    }

    private static string? ValidateName(string? name)
    {
        return string.IsNullOrWhiteSpace(name) || !ScriptUserRule.IsValidName(name.Trim())
            ? "用户名不能为空且不能包含非法字符"
            : Limits.CheckNameBytes(name.Trim(), 128, "用户名");
    }

    private static string? ValidateRemark(string? remark)
    {
        return Limits.CheckNameBytes(remark?.Trim() ?? "", 512, "备注");
    }

    private static string? ValidateRunDays(int value)
    {
        return value >= -1 ? null : "运行天数只能为 -1（永久）或 0 及以上的整数";
    }

    private static string? ValidateSmtp(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : SmtpSender.ValidateRecipients(value.Trim());
    }

    private static bool HasAvatar(string userId)
    {
        string dir = Path.Combine(AppPaths.UserAssetsDir, userId);
        return Directory.Exists(dir) && Directory.GetFiles(dir, "avatar.*").Any();
    }

    private static void DeleteAvatarFiles(string userId)
    {
        string dir = Path.Combine(AppPaths.UserAssetsDir, userId);
        if (!Directory.Exists(dir))
        {
            return;
        }
        foreach (string file in Directory.GetFiles(dir, "avatar.*"))
        {
            try { File.Delete(file); } catch (Exception ex) { Logger.Warn($"[警告] 清理用户头像失败（{file}）：{ex.Message}"); }
        }
        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { }
    }

    private static bool HasMatchingMagic(string mime, byte[] data)
    {
        return mime switch
        {
            "image/png" => data.Length >= 8
                && data.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            "image/jpeg" => data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF,
            "image/webp" => data.Length >= 12
                && data.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                && data.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false,
        };
    }

    private sealed class UserPayload
    {
        public string Name { get; set; } = "";

        public string? Remark { get; set; }

        public bool AutoCheckInEnabled { get; set; }
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
            };
        }
    }

    private sealed class AvatarPayload
    {
        public string MimeType { get; set; } = "";

        public string Data { get; set; } = "";
    }
}
