using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using NexusPipeline.Extensibility;
using NexusPipeline.App.Abstractions;
using NexusPipeline.App.Commands;
using NexusPipeline.App.Contracts;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
using NexusPipeline.Services.Execution;
using NexusPipeline.Utilities;

namespace NexusPipeline.Web;

[ApiRoute("scripts")]
internal static class ApiScriptsHandler
{
    private const uint LoadLibraryAsDataFile = 0x2;

    private const uint LoadLibraryAsImageResource = 0x20;

    private const int RtGroupIcon = 14;

    private const int RtIcon = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumResourceNameProc callback, IntPtr param);

    private delegate bool EnumResourceNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr resource);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] data, uint bytes, bool icon, uint version, int cxDesired, int cyDesired, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static readonly Dictionary<string, byte[]> IconCache = new();

    private static readonly object IconSync = new();

    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        if (method == "GET" && seg.Length == 1)
        {
            Audit.Log(Audit.Web, "查询脚本实例列表", $"{ctx.Scripts.Count} 条");
            // 深拷贝快照后序列化——避免枚举/序列化与并发修改冲突（「集合已修改」/越界异常）。
            List<NexusUser> users = ctx.SnapshotUsers();
            List<ScriptInstance> snapshot = ctx.SnapshotScripts()
                .OrderBy(script => script.Index)
                .Select(script => ProjectScriptForCompatibility(script, users))
                .ToList();
            await HttpHelper.WriteJsonAsync(context, snapshot).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && seg.Length == 2
            && !seg[1].Equals("edit-sessions", StringComparison.OrdinalIgnoreCase))
        {
            string scriptId = Uri.UnescapeDataString(seg[1]);
            ScriptInstance? script = ctx.FindScript(scriptId);
            if (script is null)
            {
                await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(
                context,
                ProjectScriptForCompatibility(script, ctx.SnapshotUsers())).ConfigureAwait(false);
            return;
        }
        if (method == "PUT" && seg.Length == 2 && seg[1].Equals("order", StringComparison.OrdinalIgnoreCase))
        {
            await HandleReorderScriptsAsync(context, body).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && seg.Length == 2 && seg[1].Equals("edit-sessions", StringComparison.OrdinalIgnoreCase))
        {
            var sessions = UserConfigManager.EditSessions.Values
                .Select(session => new { scriptId = session.Script.Id, scriptName = session.Script.Name, userName = session.User.Name })
                .ToList();
            await HttpHelper.WriteJsonAsync(context, sessions).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && seg.Length == 3 && seg[2].Equals("icon", StringComparison.OrdinalIgnoreCase))
        {
            await HandleIconAsync(context, seg[1]).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 2 && seg[1].Equals("probe", StringComparison.OrdinalIgnoreCase))
        {
            await HandleProbeAsync(context, body).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && seg.Length == 1)
        {
            ScriptInstance? script = HttpHelper.ParseBody<ScriptInstance>(body);
            if (script is null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "脚本名称不能为空" }, 400).ConfigureAwait(false);
                return;
            }
            OperationResult<ScriptInstance> result = ScriptCommands.Create(script);
            if (!result.Succeeded)
            {
                await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(
                context,
                ProjectScriptForCompatibility(result.Value!, ctx.SnapshotUsers())).ConfigureAwait(false);
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
            OperationResult<ScriptInstance> result = ScriptCommands.Update(seg[1], update);
            if (!result.Succeeded)
            {
                await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
                return;
            }
            lock (IconSync)
            {
                IconCache.Remove(result.Value!.Id);
            }
            await HttpHelper.WriteJsonAsync(
                context,
                ProjectScriptForCompatibility(result.Value!, ctx.SnapshotUsers())).ConfigureAwait(false);
            return;
        }
        if (method == "DELETE" && seg.Length == 2)
        {
            OperationResult<ScriptInstance?> result = ScriptCommands.Delete(seg[1]);
            if (!result.Succeeded)
            {
                await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
                return;
            }
            lock (IconSync)
            {
                IconCache.Remove(seg[1]);
            }
            ScriptConfigGate.Remove(seg[1]);
            await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
            return;
        }
        await HandleScriptUsersAsync(context, method, seg, body).ConfigureAwait(false);
    }

    /// <summary>专用插件配置探测：前端简化弹窗在根目录填写后即时校验能否推导。</summary>
    private static async Task HandleProbeAsync(HttpListenerContext context, string body)
    {
        JsonNode? node = HttpHelper.ParseBody(body);
        string rootPath = node?["rootPath"]?.ToString() ?? "";
        string pluginType = node?["pluginType"]?.ToString() ?? "";
        OperationResult<ScriptProfile> result = ScriptCommands.Probe(pluginType, rootPath);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { ok = true, profile = result.Value }).ConfigureAwait(false);
    }

    /// <summary>脚本主程序图标（提取关联图标转 PNG，内存缓存；无图标/主程序无效返回 404，前端使用占位图）。</summary>
    private static async Task HandleIconAsync(HttpListenerContext context, string scriptId)
    {
        ScriptInstance? script = RuntimeContext.Instance.FindScript(scriptId);
        if (script is null || string.IsNullOrWhiteSpace(script.MainExe))
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        byte[]? icon;
        lock (IconSync)
        {
            if (!IconCache.TryGetValue(scriptId, out icon))
            {
                icon = ExtractIcon(script.MainExe);
                if (icon is not null)
                {
                    IconCache[scriptId] = icon;
                }
            }
        }
        if (icon is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        context.Response.StatusCode = 200;
        context.Response.ContentType = "image/png";
        context.Response.Headers["Cache-Control"] = "no-cache";
        // icon 响应补安全头（此前手工写响应漏 nosniff/CSP 等，与静态文件不一致）。
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.ContentLength64 = icon.Length;
        await context.Response.OutputStream.WriteAsync(icon).ConfigureAwait(false);
        context.Response.OutputStream.Close();
    }

    /// <summary>
    /// 从主程序提取最高分辨率图标（PE 资源枚举，含 256×256），无图标资源时回退系统默认关联图标。
    /// 返回 PNG 字节；均失败返回 null（前端使用占位图）。</summary>
    private static byte[]? ExtractIcon(string mainExe)
    {
        byte[]? best = ExtractBestIcon(mainExe);
        if (best is not null)
        {
            return best;
        }
        if (SystemActions.IsCommandFile(mainExe))
        {
            return null;
        }
        try
        {
            using Icon? icon = Icon.ExtractAssociatedIcon(mainExe);
            if (icon is null)
            {
                return null;
            }
            using var ms = new MemoryStream();
            icon.ToBitmap().Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>PE 资源枚举最高分辨率图标（RT_GROUP_ICON → GRPICONDIR → 最大尺寸条目）；无资源返回 null。</summary>
    private static byte[]? ExtractBestIcon(string mainExe)
    {
        IntPtr module = IntPtr.Zero;
        IntPtr iconHandle = IntPtr.Zero;
        try
        {
            module = LoadLibraryEx(mainExe, IntPtr.Zero, LoadLibraryAsDataFile | LoadLibraryAsImageResource);
            if (module == IntPtr.Zero)
            {
                return null;
            }
            int bestWidth = 0;
            int bestHeight = 0;
            byte[]? bestDirectory = null;
            int bestIconId = 0;
            var groupNames = new List<IntPtr>();
            EnumResourceNames(module, (IntPtr)RtGroupIcon, (_, _, name, _) =>
            {
                groupNames.Add(name);
                return true;
            }, IntPtr.Zero);
            foreach (IntPtr groupName in groupNames)
            {
                IntPtr resource = FindResource(module, groupName, (IntPtr)RtGroupIcon);
                if (resource == IntPtr.Zero)
                {
                    continue;
                }
                byte[]? directory = ReadBytes(module, resource);
                if (directory is null || directory.Length < 6)
                {
                    continue;
                }
                int count = BitConverter.ToUInt16(directory, 4);
                for (int index = 0; index < count; index++)
                {
                    int offset = 6 + index * 14;
                    if (offset + 14 > directory.Length)
                    {
                        break;
                    }
                    int width = directory[offset] == 0 ? 256 : directory[offset];
                    int height = directory[offset + 1] == 0 ? 256 : directory[offset + 1];
                    if (width * height > bestWidth * bestHeight)
                    {
                        bestWidth = width;
                        bestHeight = height;
                        bestDirectory = directory;
                        bestIconId = BitConverter.ToUInt16(directory, offset + 12);
                    }
                }
            }
            if (bestDirectory is null)
            {
                return null;
            }
            IntPtr iconRes = FindResource(module, (IntPtr)bestIconId, (IntPtr)RtIcon);
            if (iconRes == IntPtr.Zero)
            {
                return null;
            }
            byte[]? iconData = ReadBytes(module, iconRes);
            if (iconData is null || iconData.Length == 0)
            {
                return null;
            }
            iconHandle = CreateIconFromResourceEx(iconData, (uint)iconData.Length, true, 0x00030000, 0, 0, 0);
            if (iconHandle == IntPtr.Zero)
            {
                return null;
            }
            using (Icon icon = Icon.FromHandle(iconHandle))
            {
                using var ms = new MemoryStream();
                icon.ToBitmap().Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (iconHandle != IntPtr.Zero)
            {
                DestroyIcon(iconHandle);
            }
            if (module != IntPtr.Zero)
            {
                FreeLibrary(module);
            }
        }
    }

    /// <summary>读取资源数据（SizeofResource 确定大小，LoadResource → LockResource 拷贝）。</summary>
    private static byte[]? ReadBytes(IntPtr module, IntPtr resource)
    {
        uint size = SizeofResource(module, resource);
        if (size == 0 || size > 8 * 1024 * 1024)
        {
            return null;
        }
        IntPtr loaded = LoadResource(module, resource);
        if (loaded == IntPtr.Zero)
        {
            return null;
        }
        IntPtr pointer = LockResource(loaded);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }
        var buffer = new byte[size];
        Marshal.Copy(pointer, buffer, 0, (int)size);
        return buffer;
    }

    /// <summary>解析运行时启动目标（Args 首项显式路径 → 该程序；否则主程序），用于运行冲突检测。</summary>
    private static string ResolveLaunchTargetExe(ScriptInstance script)
    {
        string workingDir = string.IsNullOrWhiteSpace(script.RootPath)
            ? Path.GetDirectoryName(script.MainExe) ?? ""
            : script.RootPath;
        return SystemActions.ResolveLaunchTarget(script.MainExe, workingDir, script.Args).ExePath;
    }

    private static ScriptInstance ProjectScriptForCompatibility(ScriptInstance script, IReadOnlyList<NexusUser> users)
    {
        ScriptInstance clone = script.Clone();
        if (users.Count == 0)
        {
            return clone;
        }

        List<ScriptUser> projected = users
            .OrderBy(user => user.Index)
            .SelectMany(user => user.Bindings
                .Where(binding => string.Equals(binding.ScriptInstanceId, script.Id, StringComparison.Ordinal))
                .Select(binding =>
                {
                    UserScriptBinding effective = UserBindingOverrideResolver.Resolve(user, binding);
                    return new ScriptUser
                    {
                        Name = user.Name,
                        Enabled = effective.Participates,
                        PreRunScript = effective.PreRunScript,
                        PreRunOnceOnly = effective.PreRunOnceOnly,
                        PostRunScript = effective.PostRunScript,
                        PostRunOnFinalOnly = effective.PostRunOnFinalOnly,
                    };
                }))
            .ToList();
        // 迁移完成后嵌套用户已不再是权威数据；这里仅为旧客户端保留只读投影。
        clone.Users = projected;
        return clone;
    }

    private static Task HandleScriptUsersAsync(HttpListenerContext context, string method, string[] seg, string body)
    {
        return HandleGlobalScriptUsersCompatibilityAsync(context, method, seg, body);
    }
    private static async Task HandleGlobalScriptUsersCompatibilityAsync(
        HttpListenerContext context,
        string method,
        string[] seg,
        string body)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        if (seg.Length < 3 || !seg[2].Equals("users", StringComparison.OrdinalIgnoreCase))
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }

        ScriptInstance? script = ctx.FindScript(seg[1]);
        if (script is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }

        if (seg.Length == 3 && method == "GET")
        {
            await HttpHelper.WriteJsonAsync(context, ProjectScriptForCompatibility(script, ctx.SnapshotUsers())).ConfigureAwait(false);
            return;
        }

        if (seg.Length == 3 && method == "POST")
        {
            ScriptUser? payload = HttpHelper.ParseBody<ScriptUser>(body);
            if (payload is null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "用户名不能为空且不能包含非法字符" }, 400).ConfigureAwait(false);
                return;
            }
            OperationResult<NexusUser> result = UserCommands.AddCompatibilityBinding(script.Id, payload);
            if (!result.Succeeded)
            {
                await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(context, ProjectScriptForCompatibility(script, ctx.SnapshotUsers())).ConfigureAwait(false);
            return;
        }

        if (seg.Length == 4 && method == "PUT" && seg[3].Equals("order", StringComparison.OrdinalIgnoreCase))
        {
            await ReorderGlobalUsersForCompatibilityAsync(context, script, body).ConfigureAwait(false);
            return;
        }

        if (seg.Length == 4 && method == "PUT")
        {
            string oldName = Uri.UnescapeDataString(seg[3]);
            ScriptUser? payload = HttpHelper.ParseBody<ScriptUser>(body);
            if (payload is null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "用户名不能为空且不能包含非法字符" }, 400).ConfigureAwait(false);
                return;
            }
            OperationResult<NexusUser> result = UserCommands.UpdateCompatibilityBinding(script.Id, oldName, payload);
            if (!result.Succeeded)
            {
                await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(context, ProjectScriptForCompatibility(script, ctx.SnapshotUsers())).ConfigureAwait(false);
            return;
        }

        if (seg.Length == 4 && method == "DELETE")
        {
            string userName = Uri.UnescapeDataString(seg[3]);
            OperationResult<bool> result = UserCommands.DeleteCompatibilityBinding(script.Id, userName);
            if (!result.Succeeded)
            {
                await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
                return;
            }
            await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
            return;
        }

        if (seg.Length == 5 && method == "POST" && seg[4].Equals("edit-config", StringComparison.OrdinalIgnoreCase))
        {
            await HandleEditConfigAsync(context, seg, body).ConfigureAwait(false);
            return;
        }
        await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    private static async Task ReorderGlobalUsersForCompatibilityAsync(HttpListenerContext context, ScriptInstance script, string body)
    {
        JsonNode? node = HttpHelper.ParseBody(body);
        List<string>? names = node?["names"] is JsonArray array
            ? array.Select(item => item?.ToString() ?? "").ToList()
            : null;
        OperationResult<bool> result = UserCommands.ReorderCompatibility(script.Id, names);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(
            context,
            ProjectScriptForCompatibility(script, RuntimeContext.Instance.SnapshotUsers())).ConfigureAwait(false);
    }

    /// <summary>脚本实例顺序重排：请求体携带完整 id 名单，与现有集合完全一致时按新顺序重赋 Index 落盘。</summary>
    private static async Task HandleReorderScriptsAsync(HttpListenerContext context, string body)
    {
        JsonNode? node = HttpHelper.ParseBody(body);
        List<string>? ids = node?["ids"] is JsonArray array
            ? array.Select(item => item?.ToString() ?? "").ToList()
            : null;
        OperationResult<bool> result = ScriptCommands.Reorder(ids);
        if (!result.Succeeded)
        {
            await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }

    internal static Task HandleEditConfigByUserIdAsync(
        HttpListenerContext context,
        string scriptId,
        string userId,
        string body)
    {
        return HandleEditConfigAsync(
            context,
            new[] { "scripts", scriptId, "users", Uri.EscapeDataString(userId), "edit-config" },
            body);
    }

    private static async Task HandleEditConfigAsync(HttpListenerContext context, string[] seg, string body)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        string scriptId = seg[1];
        string userReference = Uri.UnescapeDataString(seg[3]);
        string action = HttpHelper.ParseBody(body).Get("action").Str();

        if (action == "start")
        {
            OperationResult<ConfigEditStarted> result =
                ConfigEditCommands.Start(ctx, scriptId, userReference);
            if (!result.Succeeded)
            {
                await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
                return;
            }

            await HttpHelper.WriteJsonAsync(
                context,
                new { ok = true, pid = result.Value!.ProcessId }).ConfigureAwait(false);
            return;
        }

        if (action == "done" || action == "cancel")
        {
            OperationResult<bool> result =
                ConfigEditCommands.Complete(ctx, scriptId, userReference, action);
            if (!result.Succeeded)
            {
                await ApplicationErrorResponse.WriteAsync(context, result.Error!).ConfigureAwait(false);
                return;
            }

            await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
            return;
        }

        await HttpHelper.WriteJsonAsync(
            context,
            new { error = "未知操作：" + action },
            400).ConfigureAwait(false);
    }
}
