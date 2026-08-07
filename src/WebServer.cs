using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusPipeline;

public class WebServer : IDisposable
{
    private readonly HttpListener _listener = new();

    private CancellationTokenSource? _cts;

    private Task? _loop;

    private int _port;

    public int Port => _port;

    public void Start(int port)
    {
        _port = port;
        string prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Clear();
        _listener.Prefixes.Add(prefix);
        _cts = new CancellationTokenSource();
        _listener.Start();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        Logger.Info($"Web 服务已启动：{prefix}");
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener.Stop();
        }
        catch
        {
        }
        Logger.Info("Web 服务已停止。");
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(context, token));
        }
    }

    private static async Task HandleAsync(HttpListenerContext context, CancellationToken token)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            string method = context.Request.HttpMethod;
            if (!(method == "GET" && path == "/api/status"))
            {
                Logger.Debug($"[Web] {method} {path}");
            }
            if (path == "/" || path == "/index.html")
            {
                ServeFile(context, Path.Combine(AppPaths.WebRootDir, "index.html"));
                return;
            }
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleApiAsync(context, method, path, token).ConfigureAwait(false);
                return;
            }
            string filePath = Path.Combine(AppPaths.WebRootDir, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            ServeFile(context, filePath);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Web] 请求处理异常：{ex.Message}");
            try
            {
                context.Response.StatusCode = 500;
                await WriteJsonAsync(context, new { error = ex.Message }).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static async Task HandleApiAsync(HttpListenerContext context, string method, string path, CancellationToken token)
    {
        string body = "";
        if (context.Request.HasEntityBody)
        {
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        string[] segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            await NotFoundAsync(context).ConfigureAwait(false);
            return;
        }

        switch (segments[0].ToLowerInvariant())
        {
            case "api":
                await RouteApiAsync(context, method, segments.Skip(1).ToArray(), body).ConfigureAwait(false);
                break;
            default:
                await NotFoundAsync(context).ConfigureAwait(false);
                break;
        }
    }

    private static async Task RouteApiAsync(HttpListenerContext context, string method, string[] seg, string body)
    {
        if (seg.Length == 0)
        {
            await NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        string resource = seg[0].ToLowerInvariant();
        switch (resource)
        {
            case "status":
                if (method == "GET")
                {
                    await WriteJsonAsync(context, BuildStatus()).ConfigureAwait(false);
                }
                else
                {
                    await MethodNotAllowedAsync(context).ConfigureAwait(false);
                }
                return;
            case "scripts":
                await HandleScriptsAsync(context, method, seg, body).ConfigureAwait(false);
                return;
            case "queues":
                await HandleQueuesAsync(context, method, seg, body).ConfigureAwait(false);
                return;
            case "dispatch":
                await HandleDispatchAsync(context, method, seg, body).ConfigureAwait(false);
                return;
            case "cancel":
                if (method == "POST")
                {
                    JsonNode? node = ParseBody(body);
                    string runId = node.Get("runId").Str();
                    try
                    {
                        RuntimeContext.Instance.Center.Cancel(runId, Audit.Web);
                        await WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await WriteJsonAsync(context, new { ok = false, error = ex.Message }, 400).ConfigureAwait(false);
                    }
                }
                else
                {
                    await MethodNotAllowedAsync(context).ConfigureAwait(false);
                }
                return;
            case "history":
                await HandleHistoryAsync(context, method, seg, body).ConfigureAwait(false);
                return;
            case "settings":
                await HandleSettingsAsync(context, method, seg, body).ConfigureAwait(false);
                return;
            case "plugins":
                await HandlePluginsAsync(context, method, seg, body).ConfigureAwait(false);
                return;
            case "logs":
                if (method == "GET")
                {
                    Audit.Log(Audit.Web, "查询运行日志尾部");
                    await WriteJsonAsync(context, ReadLogTail(60)).ConfigureAwait(false);
                }
                else
                {
                    await MethodNotAllowedAsync(context).ConfigureAwait(false);
                }
                return;
            case "fs":
                await HandleFsAsync(context, method, seg, body).ConfigureAwait(false);
                return;
            default:
                await NotFoundAsync(context).ConfigureAwait(false);
                return;
        }
    }

    private static object BuildStatus()
    {
        AppSettings settings = RuntimeContext.Instance.Settings;
        var next = RuntimeContext.Instance.Scheduler.NextTrigger();
        return new
        {
            time = DateTime.Now,
            lightweightMode = settings.LightweightMode,
            webPort = settings.WebPort,
            version = typeof(WebServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            scriptCount = RuntimeContext.Instance.Scripts.Count,
            queueCount = RuntimeContext.Instance.Queues.Count,
            nextSchedule = next is null ? null : new { queueName = next.Value.QueueName, time = next.Value.TriggerTime },
            notifyStats = new
            {
                enabledScripts = RuntimeContext.Instance.Scripts.Count(script => script.NotifyEnabled),
                enabledQueues = RuntimeContext.Instance.Queues.Count(queue => queue.NotifyEnabled),
            },
            running = RuntimeContext.Instance.Center.Active.Select(exec => new
            {
                exec.Id,
                exec.Kind,
                exec.TargetId,
                exec.TargetName,
                exec.Mode,
                exec.Status,
                exec.StartedAt,
                exec.FinishedAt,
                exec.TotalTasks,
                exec.DoneTasks,
                exec.CurrentScriptName,
                exec.CurrentStatus,
                exec.CurrentAttempt,
                exec.CurrentMaxAttempts,
                logTail = exec.LogTail(60),
            }),
            plugins = RuntimeContext.Instance.Plugins.Plugins.Select(plugin => new
            {
                plugin.Name,
                plugin.DisplayName,
                plugin.Description,
                plugin.Version,
                plugin.IsBuiltIn,
                enabled = RuntimeContext.Instance.Plugins.IsEnabled(plugin.Name),
            }),
        };
    }

    private static async Task HandleScriptsAsync(HttpListenerContext context, string method, string[] seg, string body)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        if (method == "GET" && seg.Length == 1)
        {
            Audit.Log(Audit.Web, "查询脚本实例列表", $"{ctx.Scripts.Count} 条");
            await WriteJsonAsync(context, ctx.Scripts).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 1)
        {
            ScriptInstance? script = ParseBody<ScriptInstance>(body);
            if (script is null || string.IsNullOrWhiteSpace(script.Name))
            {
                await WriteJsonAsync(context, new { error = "脚本名称不能为空" }, 400).ConfigureAwait(false);
                return;
            }
            if (string.IsNullOrWhiteSpace(script.Id) || ctx.FindScript(script.Id) is null)
            {
                script.Id = Guid.NewGuid().ToString("N");
            }
            ctx.Scripts.Add(script);
            ctx.SaveScripts();
            Audit.Log(Audit.Web, "添加脚本实例", $"{script.Name}（id={script.Id}）");
            await WriteJsonAsync(context, script).ConfigureAwait(false);
            return;
        }
        if (method == "PUT" && seg.Length == 2)
        {
            ScriptInstance? update = ParseBody<ScriptInstance>(body);
            ScriptInstance? existing = ctx.FindScript(seg[1]);
            if (update is null || existing is null)
            {
                await NotFoundAsync(context).ConfigureAwait(false);
                return;
            }
            update.Id = existing.Id;
            int index = ctx.Scripts.IndexOf(existing);
            ctx.Scripts[index] = update;
            ctx.SaveScripts();
            Audit.Log(Audit.Web, "修改脚本实例", $"{update.Name}（id={update.Id}）");
            await WriteJsonAsync(context, update).ConfigureAwait(false);
            return;
        }
        if (method == "DELETE" && seg.Length == 2)
        {
            ScriptInstance? removed = ctx.FindScript(seg[1]);
            if (removed is not null)
            {
                SemaphoreSlim gate = ScriptConfigGate.Get(seg[1]);
                if (!gate.Wait(0))
                {
                    await WriteJsonAsync(context, new { error = "脚本正在运行或编辑配置中，无法删除" }, 409).ConfigureAwait(false);
                    return;
                }
                try
                {
                    UserConfigManager.RemoveScriptData(seg[1]);
                }
                finally
                {
                    gate.Release();
                }
            }
            ctx.Scripts.RemoveAll(script => script.Id == seg[1]);
            ctx.SaveScripts();
            Audit.Log(Audit.Web, "删除脚本实例", removed is null ? $"id={seg[1]}（不存在）" : $"{removed.Name}（id={seg[1]}）");
            await WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
            return;
        }
        await HandleScriptUsersAsync(context, method, seg, body).ConfigureAwait(false);
    }

    private static async Task HandleFsAsync(HttpListenerContext context, string method, string[] seg, string body)
    {
        if (seg.Length < 2)
        {
            await MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        if (seg[1].Equals("browse", StringComparison.OrdinalIgnoreCase) && method == "GET")
        {
            await HandleFsBrowseAsync(context).ConfigureAwait(false);
            return;
        }
        await MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    private static async Task HandleFsBrowseAsync(HttpListenerContext context)
    {
        string? path = context.Request.QueryString["path"];
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady || d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
                    .Select(d => d.RootDirectory.FullName)
                    .OrderBy(d => d)
                    .ToList();
                await WriteJsonAsync(context, new { path = "", parent = (string?)null, dirs = drives, files = Array.Empty<string>() }).ConfigureAwait(false);
                return;
            }
            if (!Directory.Exists(path))
            {
                await WriteJsonAsync(context, new { error = "目录不存在：" + path }, 400).ConfigureAwait(false);
                return;
            }
            var dirs = Directory.EnumerateDirectories(path).OrderBy(d => d).ToList();
            var files = Directory.EnumerateFiles(path).OrderBy(f => f).ToList();
            string? parent = Directory.GetParent(path)?.FullName;
            await WriteJsonAsync(context, new { path, parent, dirs, files }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context, new { error = "读取目录失败：" + ex.Message }, 400).ConfigureAwait(false);
        }
    }

    /// <summary>系统原生文件/文件夹选择对话框（STA 线程显示，服务端弹窗，返回所选路径；用户取消返回 path=null）。</summary>
    private static async Task HandleScriptUsersAsync(HttpListenerContext context, string method, string[] seg, string body)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        if (seg.Length >= 3 && seg[2].Equals("users", StringComparison.OrdinalIgnoreCase))
        {
            if (seg.Length == 3 && method == "POST")
            {
                ScriptInstance? script = ctx.FindScript(seg[1]);
                if (script is null)
                {
                    await NotFoundAsync(context).ConfigureAwait(false);
                    return;
                }
                ScriptUser? user = ParseBody<ScriptUser>(body);
                if (user is null || !ScriptUserRule.IsValidName(user.Name))
                {
                    await WriteJsonAsync(context, new { error = "用户名不能为空且不能包含非法字符" }, 400).ConfigureAwait(false);
                    return;
                }
                if (script.Users.Any(existing => string.Equals(existing.Name, user.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    await WriteJsonAsync(context, new { error = "用户名重复：该脚本已存在同名用户" }, 400).ConfigureAwait(false);
                    return;
                }
                script.Users.Add(user);
                ctx.SaveScripts();
                string? snapError = UserConfigManager.SnapshotOnAddUser(script, user.Name);
                Audit.Log(Audit.Web, "添加用户", $"{script.Name} / {user.Name}");
                if (snapError is not null)
                {
                    Logger.Warn($"[警告] 用户「{user.Name}」初始配置快照失败：{snapError}");
                }
                await WriteJsonAsync(context, script).ConfigureAwait(false);
                return;
            }
            if (seg.Length == 4 && method == "PUT")
            {
                ScriptInstance? script = ctx.FindScript(seg[1]);
                string oldName = Uri.UnescapeDataString(seg[3]);
                if (script is null)
                {
                    await NotFoundAsync(context).ConfigureAwait(false);
                    return;
                }
                ScriptUser? existing = script.Users.FirstOrDefault(u => u.Name == oldName);
                if (existing is null)
                {
                    await NotFoundAsync(context).ConfigureAwait(false);
                    return;
                }
                ScriptUser? update = ParseBody<ScriptUser>(body);
                if (update is null || !ScriptUserRule.IsValidName(update.Name))
                {
                    await WriteJsonAsync(context, new { error = "用户名不能为空且不能包含非法字符" }, 400).ConfigureAwait(false);
                    return;
                }
                if (!string.Equals(oldName, update.Name, StringComparison.OrdinalIgnoreCase)
                    && script.Users.Any(u => !ReferenceEquals(u, existing) && string.Equals(u.Name, update.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    await WriteJsonAsync(context, new { error = "用户名重复：该脚本已存在同名用户" }, 400).ConfigureAwait(false);
                    return;
                }
                string oldDataUser = existing.Name;
                existing.Name = update.Name;
                existing.Enabled = update.Enabled;
                existing.PreRunScript = update.PreRunScript;
                existing.PreRunOnceOnly = update.PreRunOnceOnly;
                existing.PostRunScript = update.PostRunScript;
                existing.PostRunOnFinalOnly = update.PostRunOnFinalOnly;
                ctx.SaveScripts();
                Audit.Log(Audit.Web, "编辑用户", $"{script.Name} / {oldDataUser} → {existing.Name}");
                if (!string.Equals(oldDataUser, existing.Name, StringComparison.OrdinalIgnoreCase))
                {
                    UserConfigManager.RenameUserData(script.Id, oldDataUser, existing.Name);
                }
                await WriteJsonAsync(context, script).ConfigureAwait(false);
                return;
            }
            if (seg.Length == 4 && method == "DELETE")
            {
                ScriptInstance? script = ctx.FindScript(seg[1]);
                string userName = Uri.UnescapeDataString(seg[3]);
                if (script is null)
                {
                    await NotFoundAsync(context).ConfigureAwait(false);
                    return;
                }
                if (script.Users.All(u => u.Name != userName))
                {
                    await NotFoundAsync(context).ConfigureAwait(false);
                    return;
                }
                SemaphoreSlim gate = ScriptConfigGate.Get(seg[1]);
                if (!gate.Wait(0))
                {
                    await WriteJsonAsync(context, new { error = "脚本正在运行或编辑配置中，无法删除用户" }, 409).ConfigureAwait(false);
                    return;
                }
                try
                {
                    UserConfigManager.RemoveUserData(seg[1], userName);
                }
                finally
                {
                    gate.Release();
                }
                script.Users.RemoveAll(u => u.Name == userName);
                ctx.SaveScripts();
                Audit.Log(Audit.Web, "删除用户", $"{script.Name} / {userName}");
                await WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
                return;
            }
            if (seg.Length == 5 && method == "POST" && seg[4].Equals("edit-config", StringComparison.OrdinalIgnoreCase))
            {
                await HandleEditConfigAsync(context, seg, body).ConfigureAwait(false);
                return;
            }
        }
        await MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    private static async Task HandleEditConfigAsync(HttpListenerContext context, string[] seg, string body)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        string scriptId = seg[1];
        string userName = Uri.UnescapeDataString(seg[3]);
        string action = ParseBody(body).Get("action").Str();
        ScriptInstance? script = ctx.FindScript(scriptId);
        if (script is null)
        {
            await NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        ScriptUser? user = script.Users.FirstOrDefault(u => u.Name == userName);
        if (user is null)
        {
            await NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(script.ConfigPath))
        {
            await WriteJsonAsync(context, new { error = "脚本未配置「配置文件路径/文件夹」" }, 400).ConfigureAwait(false);
            return;
        }
        SemaphoreSlim gate = ScriptConfigGate.Get(scriptId);
        if (action == "start")
        {
            if (!gate.Wait(0))
            {
                await WriteJsonAsync(context, new { error = "脚本正在运行或编辑配置中" }, 409).ConfigureAwait(false);
                return;
            }
            bool keepGate = false;
            try
            {
                if (!TextRules.IsExecutable(script.MainExe))
                {
                    await WriteJsonAsync(context, new { error = "脚本主程序路径错误或不是可执行文件" }, 400).ConfigureAwait(false);
                    return;
                }
                string? prepError = UserConfigManager.PrepareForEdit(script.Id, user.Name, script.ConfigPath);
                if (prepError is not null)
                {
                    await WriteJsonAsync(context, new { error = "配置交换失败：" + prepError }, 400).ConfigureAwait(false);
                    return;
                }
                Process? proc;
                try
                {
                    proc = SystemActions.StartVisible(script.MainExe,
                        string.IsNullOrWhiteSpace(script.RootPath) ? Path.GetDirectoryName(script.MainExe) ?? "" : script.RootPath);
                }
                catch (Exception ex)
                {
                    UserConfigManager.CancelEdit(script.Id, user.Name, script.ConfigPath);
                    await WriteJsonAsync(context, new { error = "主程序启动失败：" + ex.Message + "，配置已还原，可修正后重试" }, 400).ConfigureAwait(false);
                    return;
                }
                UserConfigManager.EditSessions[scriptId] = new EditSession
                {
                    Script = script,
                    User = user,
                    Process = proc,
                    Mark = new ConfigSessionMark
                    {
                        ScriptId = script.Id,
                        UserName = user.Name,
                        ConfigPath = script.ConfigPath,
                        OriginalKind = PathKindUtil.Text(PathKindUtil.KindOf(script.ConfigPath)),
                        Phase = "edit",
                    },
                };
                keepGate = true;
                Audit.Log(Audit.Web, "开始编辑配置", $"{script.Name} / {user.Name}（主程序已启动）");
                await WriteJsonAsync(context, new { ok = true, pid = proc?.Id ?? 0 }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(context, new { error = ex.Message }, 400).ConfigureAwait(false);
            }
            finally
            {
                if (!keepGate)
                {
                    gate.Release();
                }
            }
            return;
        }
        if (action == "done" || action == "cancel")
        {
            if (!UserConfigManager.EditSessions.TryRemove(scriptId, out EditSession? session))
            {
                await WriteJsonAsync(context, new { error = "没有进行中的编辑配置会话" }, 409).ConfigureAwait(false);
                return;
            }
            try
            {
                if (session.Process is not null && !session.Process.HasExited)
                {
                    SystemActions.KillTree(session.Process.Id);
                }
                string? swapError = action == "done"
                    ? UserConfigManager.CommitEdit(scriptId, user.Name, script.ConfigPath)
                    : UserConfigManager.CancelEdit(scriptId, user.Name, script.ConfigPath);
                if (swapError is not null)
                {
                    await WriteJsonAsync(context, new { error = (action == "done" ? "提交" : "取消") + "失败：" + swapError }, 400).ConfigureAwait(false);
                    return;
                }
                Audit.Log(Audit.Web, action == "done" ? "完成编辑配置" : "取消编辑配置", $"{script.Name} / {user.Name}");
                await WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(context, new { error = ex.Message }, 400).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
            return;
        }
        await WriteJsonAsync(context, new { error = "未知操作：" + action }, 400).ConfigureAwait(false);
    }

    private static async Task HandleQueuesAsync(HttpListenerContext context, string method, string[] seg, string body)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        if (method == "GET" && seg.Length == 1)
        {
            Audit.Log(Audit.Web, "查询调度队列列表", $"{ctx.Queues.Count} 条");
            await WriteJsonAsync(context, ctx.Queues).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 1)
        {
            DispatchQueue? queue = ParseBody<DispatchQueue>(body);
            if (queue is null || string.IsNullOrWhiteSpace(queue.Name))
            {
                await WriteJsonAsync(context, new { error = "队列名称不能为空" }, 400).ConfigureAwait(false);
                return;
            }
            if (string.IsNullOrWhiteSpace(queue.Id) || ctx.FindQueue(queue.Id) is null)
            {
                queue.Id = Guid.NewGuid().ToString("N");
            }
            NormalizeQueue(queue);
            ctx.Queues.Add(queue);
            ctx.SaveQueues();
            Audit.Log(Audit.Web, "添加调度队列", $"{queue.Name}（id={queue.Id}，任务 {queue.Tasks.Count} 项）");
            await WriteJsonAsync(context, queue).ConfigureAwait(false);
            return;
        }
        if (method == "PUT" && seg.Length == 2)
        {
            DispatchQueue? update = ParseBody<DispatchQueue>(body);
            DispatchQueue? existing = ctx.FindQueue(seg[1]);
            if (update is null || existing is null)
            {
                await NotFoundAsync(context).ConfigureAwait(false);
                return;
            }
            update.Id = existing.Id;
            NormalizeQueue(update);
            int index = ctx.Queues.IndexOf(existing);
            ctx.Queues[index] = update;
            ctx.SaveQueues();
            Audit.Log(Audit.Web, "修改调度队列", $"{update.Name}（id={update.Id}，任务 {update.Tasks.Count} 项）");
            await WriteJsonAsync(context, update).ConfigureAwait(false);
            return;
        }
        if (method == "DELETE" && seg.Length == 2)
        {
            DispatchQueue? removed = ctx.FindQueue(seg[1]);
            ctx.Queues.RemoveAll(queue => queue.Id == seg[1]);
            ctx.SaveQueues();
            Audit.Log(Audit.Web, "删除调度队列", removed is null ? $"id={seg[1]}（不存在）" : $"{removed.Name}（id={seg[1]}）");
            await WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
            return;
        }
        await MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    private static void NormalizeQueue(DispatchQueue queue)
    {
        if (!QueueRule.IsValidAutoRunMode(queue.AutoRunMode))
        {
            queue.AutoRunMode = "scheduled";
        }
        if (!QueueRule.IsValidCompletionAction(queue.CompletionAction))
        {
            queue.CompletionAction = "none";
        }
        int index = 0;
        foreach (QueueTask task in queue.Tasks.OrderBy(task => task.Index))
        {
            task.Index = index++;
            if (string.IsNullOrWhiteSpace(task.Id))
            {
                task.Id = Guid.NewGuid().ToString("N");
            }
        }
        foreach (QueueTimeSet timeSet in queue.TimeSets)
        {
            if (string.IsNullOrWhiteSpace(timeSet.Id))
            {
                timeSet.Id = Guid.NewGuid().ToString("N");
            }
            if (!TimeOnly.TryParseExact(timeSet.Time, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            {
                timeSet.Time = "08:00";
            }
        }
    }

    private static async Task HandleDispatchAsync(HttpListenerContext context, string method, string[] seg, string body)
    {
        if (method != "POST")
        {
            await MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        JsonNode? node = ParseBody(body);
        string mode = node.Get("mode").Str();
        if (mode != "auto")
        {
            mode = "manual";
        }
        try
        {
            if (seg.Length >= 2 && seg[1].ToLowerInvariant() == "script")
            {
                string scriptId = node.Get("scriptId").Str();
                string userName = node.Get("userName").Str();
                RunningExecution exec = RuntimeContext.Instance.Center.StartScript(scriptId, mode, Audit.Web, userName);
                await WriteJsonAsync(context, new { runId = exec.Id, ok = true }).ConfigureAwait(false);
                return;
            }
            if (seg.Length >= 2 && seg[1].ToLowerInvariant() == "queue")
            {
                string queueId = node.Get("queueId").Str();
                RunningExecution exec = RuntimeContext.Instance.Center.StartQueue(queueId, mode, Audit.Web);
                await WriteJsonAsync(context, new { runId = exec.Id, ok = true }).ConfigureAwait(false);
                return;
            }
            await NotFoundAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context, new { ok = false, error = ex.Message }, 400).ConfigureAwait(false);
        }
    }

    private static async Task HandleHistoryAsync(HttpListenerContext context, string method, string[] seg, string body)
    {
        if (method != "GET")
        {
            await MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        if (seg.Length == 2 && seg[1].ToLowerInvariant() == "detail")
        {
            string id = context.Request.QueryString["id"] ?? "";
            if (string.IsNullOrWhiteSpace(id))
            {
                await WriteJsonAsync(context, new { error = "缺少记录 ID" }, 400).ConfigureAwait(false);
                return;
            }
            RunRecord? record = RuntimeContext.Instance.History.FindById(id);
            if (record is null)
            {
                await WriteJsonAsync(context, new { error = "记录不存在" }, 404).ConfigureAwait(false);
                return;
            }
            Audit.Log(Audit.Web, "查询运行详情", $"{record.ScriptName}（{record.StartTime:yyyy-MM-dd HH:mm:ss}）");
            var log = RuntimeContext.Instance.History.ReadScriptLog(record);
            await WriteJsonAsync(context, new
            {
                record,
                logTail = log is null ? null : TextRules.TakeTail(log.Value.LogText, 200),
                logTotalLines = log?.TotalLines ?? 0,
            }).ConfigureAwait(false);
            return;
        }
        string? scriptId = context.Request.QueryString["scriptId"];
        string? queueId = context.Request.QueryString["queueId"];
        int days = int.TryParse(context.Request.QueryString["days"], out int d) ? d : 3;
        if (days < 1)
        {
            days = 1;
        }
        if (days > 31)
        {
            days = 31;
        }
        List<RunRecord> records = RuntimeContext.Instance.History.Query(
            DateTime.Today.AddDays(-(days - 1)), DateTime.Now.AddMinutes(5),
            string.IsNullOrWhiteSpace(scriptId) ? null : scriptId,
            string.IsNullOrWhiteSpace(queueId) ? null : queueId);
        Audit.Log(Audit.Web, "查询历史记录", $"{records.Count} 条（{days} 天）");
        await WriteJsonAsync(context, records).ConfigureAwait(false);
    }

    private static async Task HandleSettingsAsync(HttpListenerContext context, string method, string[] seg, string body)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        if (method == "GET")
        {
            Audit.Log(Audit.Web, "查询设置");
            await WriteJsonAsync(context, new
            {
                settings = MaskedSettings(ctx.Settings),
                status = new
                {
                    webhook = WebhookSender.Status(ctx.Settings),
                    smtp = SmtpSender.Status(ctx.Settings),
                    channels = new
                    {
                        webhookEnabled = ctx.Settings.WebhookEnabled,
                        smtpEnabled = ctx.Settings.SmtpEnabled,
                    },
                    autoStart = TaskRegistration.IsRegistered(),
                },
            }).ConfigureAwait(false);
            return;
        }
        if (method == "PUT" && seg.Length == 1)
        {
            JsonNode? node = ParseBody(body);
            if (node is null)
            {
                await WriteJsonAsync(context, new { error = "请求体无效" }, 400).ConfigureAwait(false);
                return;
            }
            AppSettings current = ctx.Settings;
            if (node.Get("autoStart") is not null)
            {
                current.AutoStart = node.Get("autoStart").Bool(current.AutoStart);
            }
            if (node.Get("minimizeToTray") is not null)
            {
                current.MinimizeToTray = node.Get("minimizeToTray").Bool(current.MinimizeToTray);
            }
            if (node.Get("lightweightMode") is not null)
            {
                current.LightweightMode = node.Get("lightweightMode").Bool(current.LightweightMode);
            }
            if (node.Get("autoOpenBrowser") is not null)
            {
                current.AutoOpenBrowser = node.Get("autoOpenBrowser").Bool(current.AutoOpenBrowser);
            }
            if (node.Get("historyRetentionDays") is not null)
            {
                int days = node.Get("historyRetentionDays").Int(current.HistoryRetentionDays);
                if (days >= 1)
                {
                    current.HistoryRetentionDays = days;
                }
            }
            if (node.Get("webPort") is not null)
            {
                int port = node.Get("webPort").Int(current.WebPort);
                if (port is >= 1024 and <= 65535)
                {
                    current.WebPort = port;
                }
            }
            if (node.Get("sendStrategy") is not null)
            {
                current.SendStrategy = node.Get("sendStrategy").Str();
            }
            if (node.Get("webhookEnabled") is not null)
            {
                current.WebhookEnabled = node.Get("webhookEnabled").Bool(current.WebhookEnabled);
            }
            if (node.Get("smtpEnabled") is not null)
            {
                current.SmtpEnabled = node.Get("smtpEnabled").Bool(current.SmtpEnabled);
            }
            if (node.Get("webhookType") is not null)
            {
                current.WebhookType = node.Get("webhookType").Str();
            }
            if (node.Get("webhookTemplate") is not null)
            {
                current.WebhookTemplate = node.Get("webhookTemplate").Str();
            }
            if (node.Get("webhookTimeout") is not null)
            {
                int timeout = node.Get("webhookTimeout").Int(current.WebhookTimeout);
                if (timeout >= 1)
                {
                    current.WebhookTimeout = timeout;
                }
            }
            if (node.Get("smtpHost") is not null)
            {
                current.SmtpHost = node.Get("smtpHost").Str();
            }
            if (node.Get("smtpPort") is not null)
            {
                int port = node.Get("smtpPort").Int(current.SmtpPort);
                if (port is >= 1 and <= 65535)
                {
                    current.SmtpPort = port;
                }
            }
            if (node.Get("smtpSecure") is not null)
            {
                current.SmtpSecure = node.Get("smtpSecure").Str();
            }
            if (node.Get("smtpUser") is not null)
            {
                current.SmtpUser = node.Get("smtpUser").Str();
            }
            if (node.Get("smtpFrom") is not null)
            {
                current.SmtpFrom = node.Get("smtpFrom").Str();
            }
            if (node.Get("smtpTo") is not null)
            {
                current.SmtpTo = node.Get("smtpTo").Str();
            }
            if (node.Get("smtpSubjectPrefix") is not null)
            {
                current.SmtpSubjectPrefix = node.Get("smtpSubjectPrefix").Str();
            }
            if (node.Get("smtpTimeout") is not null)
            {
                int timeout = node.Get("smtpTimeout").Int(current.SmtpTimeout);
                if (timeout >= 1)
                {
                    current.SmtpTimeout = timeout;
                }
            }
            if (node.Get("logLevel") is not null)
            {
                string level = node.Get("logLevel").Str().Trim().ToLowerInvariant();
                if (LogLevelUtil.IsValid(level))
                {
                    current.LogLevel = level;
                }
            }
            ConfigStore.Save(current);
            TaskRegistration.SyncWithSettings(current);
            string secretDetail = "";
            if (node.Get("secretKey") is not null && node.Get("secretValue") is not null)
            {
                string key = node.Get("secretKey").Str();
                string value = node.Get("secretValue").Str();
                if (key is "webhookUrl" or "webhookSecret" or "smtpPassword")
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        ClearSecret(current, key);
                        secretDetail = $"，清除密钥 {key}";
                    }
                    else
                    {
                        SetSecret(current, key, value);
                        secretDetail = $"，更新密钥 {key}";
                    }
                    ConfigStore.Save(current);
                }
            }
            Audit.Log(Audit.Web, "保存设置", $"WebPort={current.WebPort}，AutoStart={(current.AutoStart ? "开" : "关")}，轻量={(current.LightweightMode ? "开" : "关")}{secretDetail}");
            await WriteJsonAsync(context, new { ok = true, settings = MaskedSettings(current) }).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 2 && seg[1].ToLowerInvariant() == "test")
        {
            AppSettings settings = ctx.Settings;
            string text = $"[NexusPipeline] 通知测试\r\n时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n如果你收到这条消息，说明通知渠道配置正确。";
            bool ok = await NotifySender.SendAsync(settings, text).ConfigureAwait(false);
            Audit.Log(Audit.Web, "发送测试通知", ok ? "成功" : "失败");
            await WriteJsonAsync(context, new { ok }).ConfigureAwait(false);
            return;
        }
        await MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    private static object MaskedSettings(AppSettings settings)
    {
        return new
        {
            settings.AutoStart,
            settings.MinimizeToTray,
            settings.LightweightMode,
            settings.AutoOpenBrowser,
            settings.HistoryRetentionDays,
            settings.WebPort,
            settings.SendStrategy,
            settings.WebhookEnabled,
            settings.SmtpEnabled,
            settings.WebhookType,
            webhookUrl = SecretStore.IsEncrypted(settings.WebhookUrl) ? "enc:***" : settings.WebhookUrl,
            webhookSecret = SecretStore.IsEncrypted(settings.WebhookSecret) ? "enc:***" : settings.WebhookSecret,
            settings.WebhookTemplate,
            settings.WebhookTimeout,
            settings.SmtpHost,
            settings.SmtpPort,
            settings.SmtpSecure,
            settings.SmtpUser,
            smtpPassword = SecretStore.IsEncrypted(settings.SmtpPassword) ? "enc:***" : settings.SmtpPassword,
            settings.SmtpFrom,
            settings.SmtpTo,
            settings.SmtpSubjectPrefix,
            settings.SmtpTimeout,
            settings.LogLevel,
        };
    }

    private static void SetSecret(AppSettings settings, string key, string value)
    {
        string encrypted = SecretStore.Encrypt(value);
        switch (key)
        {
            case "webhookUrl":
                settings.WebhookUrl = encrypted;
                break;
            case "webhookSecret":
                settings.WebhookSecret = encrypted;
                break;
            case "smtpPassword":
                settings.SmtpPassword = encrypted;
                break;
        }
    }

    private static void ClearSecret(AppSettings settings, string key)
    {
        switch (key)
        {
            case "webhookUrl":
                settings.WebhookUrl = "";
                break;
            case "webhookSecret":
                settings.WebhookSecret = "";
                break;
            case "smtpPassword":
                settings.SmtpPassword = "";
                break;
        }
    }

    private static async Task HandlePluginsAsync(HttpListenerContext context, string method, string[] seg, string body)
    {
        if (method != "POST" || seg.Length != 3)
        {
            await MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        string name = seg[1];
        bool enabled = seg[2].ToLowerInvariant() == "enable";
        RuntimeContext.Instance.Plugins.SetEnabled(name, enabled, Audit.Web);
        await WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }

    private static List<string> ReadLogTail(int lines)
    {
        if (!File.Exists(AppPaths.LogFile))
        {
            return new List<string>();
        }
        return File.ReadAllLines(AppPaths.LogFile).TakeLast(lines).ToList();
    }

    private static void ServeFile(HttpListenerContext context, string filePath)
    {
        if (!File.Exists(filePath))
        {
            NotFoundAsync(context).GetAwaiter().GetResult();
            return;
        }
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        string contentType = extension switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
        context.Response.ContentType = contentType;
        byte[] data = File.ReadAllBytes(filePath);
        context.Response.ContentLength64 = data.Length;
        context.Response.OutputStream.Write(data, 0, data.Length);
        context.Response.OutputStream.Close();
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, object value, int statusCode = 200)
    {
        byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOpts.Web));
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = data.Length;
        await context.Response.OutputStream.WriteAsync(data).ConfigureAwait(false);
        context.Response.OutputStream.Close();
    }

    private static async Task NotFoundAsync(HttpListenerContext context)
    {
        await WriteJsonAsync(context, new { error = "未找到" }, 404).ConfigureAwait(false);
    }

    private static async Task MethodNotAllowedAsync(HttpListenerContext context)
    {
        await WriteJsonAsync(context, new { error = "请求方法不支持" }, 405).ConfigureAwait(false);
    }

    private static JsonNode? ParseBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(body);
        }
        catch
        {
            return null;
        }
    }

    private static T? ParseBody<T>(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return default;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOpts.Default);
        }
        catch
        {
            return default;
        }
    }
}
