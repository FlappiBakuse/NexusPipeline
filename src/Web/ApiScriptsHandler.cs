using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using NexusPipeline.Extensibility;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Services;
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
            // v0.7.2+（KN-04）：深拷贝快照后序列化——避免枚举/序列化与并发修改冲突（「集合已修改」/越界异常）。
            List<ScriptInstance> snapshot = ctx.SnapshotScripts().OrderBy(script => script.Index).ToList();
            await HttpHelper.WriteJsonAsync(context, snapshot).ConfigureAwait(false);
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
            if (script is null || string.IsNullOrWhiteSpace(script.Name))
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "脚本名称不能为空" }, 400).ConfigureAwait(false);
                return;
            }
            string? limitError;
            lock (ctx.DataLock)
            {
                // v0.7.2+（KN-04）：锁内完成「校验-读-写」整段，避免与并发请求/后台线程冲突。
                limitError = Limits.CheckScriptCount(ctx.Scripts.Count)
                    ?? Limits.CheckNameBytes(script.Name, Limits.Current.MaxScriptNameBytes, "脚本名称")
                    ?? Limits.CheckAttempts(script.MaxAttempts)
                    ?? Limits.CheckScriptTimeouts(script.LogStallTimeoutMinutes, script.TotalTimeoutMinutes);
                if (limitError is null)
                {
                    // v0.7.1+（KN-02）：新建一律重新生成 Id——客户端提交已存在 Id 会造成集合重复记录。
                    script.Id = Guid.NewGuid().ToString("N");
                    if (ctx.Scripts.Count > 0)
                    {
                        script.Index = ctx.Scripts.Max(item => item.Index) + 1;
                    }
                }
            }
            if (limitError is not null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = limitError }, 400).ConfigureAwait(false);
                return;
            }
            NormalizePaths(script);
            string? pluginError = string.IsNullOrWhiteSpace(script.PluginType) ? null : ApplyProfile(script);
            if (pluginError is not null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = pluginError }, 400).ConfigureAwait(false);
                return;
            }
            if (script.JudgeScriptEnabled && string.IsNullOrWhiteSpace(script.JudgeScript))
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "开启「使用判断脚本」但判断脚本代码为空" }, 400).ConfigureAwait(false);
                return;
            }
            string? pathError = Limits.CheckScriptPaths(script);
            if (pathError is not null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = pathError }, 400).ConfigureAwait(false);
                return;
            }
            lock (ctx.DataLock)
            {
                ctx.Scripts.Add(script);
                DataStore.SaveScripts(ctx.Scripts);
            }
            Audit.Log(Audit.Web, "添加脚本实例", $"{script.Name}（id={script.Id}）");
            await HttpHelper.WriteJsonAsync(context, script).ConfigureAwait(false);
            return;
        }
        if (method == "PUT" && seg.Length == 2)
        {
            ScriptInstance? update = HttpHelper.ParseBody<ScriptInstance>(body);
            ScriptInstance? existing = ctx.FindScript(seg[1]);
            if (update is null || existing is null)
            {
                await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                return;
            }
            SemaphoreSlim scriptGate = ScriptConfigGate.Get(existing.Id);
            if (!scriptGate.Wait(0))
            {
                context.Response.StatusCode = 409;
                await HttpHelper.WriteJsonAsync(context, new { error = "脚本正在运行或编辑配置中，无法修改" }, 409).ConfigureAwait(false);
                return;
            }
            try
            {
            string? limitError = Limits.CheckNameBytes(update.Name, Limits.Current.MaxScriptNameBytes, "脚本名称")
                ?? Limits.CheckAttempts(update.MaxAttempts)
                ?? Limits.CheckScriptTimeouts(update.LogStallTimeoutMinutes, update.TotalTimeoutMinutes);
            if (limitError is not null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = limitError }, 400).ConfigureAwait(false);
                return;
            }
            update.Id = existing.Id;
            update.Index = existing.Index;
            update.Users = existing.Users;
            NormalizePaths(update);
            string? pluginError = string.IsNullOrWhiteSpace(update.PluginType) ? null : ApplyProfile(update);
            if (pluginError is not null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = pluginError }, 400).ConfigureAwait(false);
                return;
            }
            if (update.JudgeScriptEnabled && string.IsNullOrWhiteSpace(update.JudgeScript))
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "开启「使用判断脚本」但判断脚本代码为空" }, 400).ConfigureAwait(false);
                return;
            }
            string? pathError = Limits.CheckScriptPaths(update);
            if (pathError is not null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = pathError }, 400).ConfigureAwait(false);
                return;
            }
            lock (IconSync)
            {
                IconCache.Remove(existing.Id);
            }
            // v0.7.2+（KN-04）：锁内完成「查找-替换-保存」整段，避免并发修改导致 IndexOf 落空/越界；锁内不做 await。
            bool notFound;
            lock (ctx.DataLock)
            {
                int index = ctx.Scripts.IndexOf(existing);
                notFound = index < 0;
                if (!notFound)
                {
                    ctx.Scripts[index] = update;
                    DataStore.SaveScripts(ctx.Scripts);
                }
            }
            if (notFound)
            {
                await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                return;
            }
            Audit.Log(Audit.Web, "修改脚本实例", $"{update.Name}（id={update.Id}）");
            await HttpHelper.WriteJsonAsync(context, update).ConfigureAwait(false);
            return;
            }
            finally
            {
                scriptGate.Release();
            }
        }
        if (method == "DELETE" && seg.Length == 2)
        {
            ScriptInstance? removed = ctx.FindScript(seg[1]);
            if (removed is not null)
            {
                SemaphoreSlim gate = ScriptConfigGate.Get(seg[1]);
                if (!gate.Wait(0))
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = "脚本正在运行或编辑配置中，无法删除" }, 409).ConfigureAwait(false);
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
            // v0.7.2+（KN-04）：锁内完成删除与保存，避免与并发请求/后台线程冲突。
            lock (ctx.DataLock)
            {
                ctx.Scripts.RemoveAll(script => script.Id == seg[1]);
                DataStore.SaveScripts(ctx.Scripts);
            }
            lock (IconSync)
            {
                IconCache.Remove(seg[1]);
            }
            ScriptConfigGate.Remove(seg[1]);
            ConfigSwapPrimitives.RemoveMutex(seg[1]);
            Audit.Log(Audit.Web, "删除脚本实例", removed is null ? $"id={seg[1]}（不存在）" : $"{removed.Name}（id={seg[1]}）");
            await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
            return;
        }
        await HandleScriptUsersAsync(context, method, seg, body).ConfigureAwait(false);
    }

    /// <summary>专用脚本实例：由专用插件按根目录推导并固化主程序/参数/配置/日志快照；失败返回错误信息。</summary>
    private static string? ApplyProfile(ScriptInstance script)
    {
        ScriptProfile? profile = RuntimeContext.Instance.Plugins.ResolveProfile(script.PluginType, script.RootPath);
        if (profile is null)
        {
            return "专用插件无法从脚本根目录推导配置（请检查脚本根目录，并确认专用插件已启用）";
        }
        script.MainExe = profile.MainExe;
        script.Args = profile.Args;
        script.ConfigPath = profile.ConfigPath;
        script.LogPath = profile.LogPath;
        script.SuccessKeywords = "";
        script.FailureKeywords = "";
        script.AutoUpdateConfig = true;
        // v0.6.0+：专项脚本判断脚本由插件固化（用户不可编辑），语言按数据化插件判断脚本扩展名（.js 内置引擎 / .py 系统解释器）。
        script.JudgeScriptEnabled = !string.IsNullOrWhiteSpace(profile.JudgeScript);
        script.JudgeScriptLanguage = string.IsNullOrWhiteSpace(profile.JudgeScriptLanguage) ? "javascript" : profile.JudgeScriptLanguage;
        script.JudgeScript = profile.JudgeScript ?? "";
        return null;
    }

    /// <summary>专用插件配置探测：前端简化弹窗在根目录填写后即时校验能否推导。</summary>
    private static async Task HandleProbeAsync(HttpListenerContext context, string body)
    {
        JsonNode? node = HttpHelper.ParseBody(body);
        string rootPath = node?["rootPath"]?.ToString() ?? "";
        string pluginType = node?["pluginType"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(pluginType))
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "缺少专用插件标识" }, 400).ConfigureAwait(false);
            return;
        }
        ScriptProfile? profile = RuntimeContext.Instance.Plugins.ResolveProfile(pluginType, StripPathQuotes(rootPath));
        if (profile is null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "无法从脚本根目录推导专用插件配置（请检查根目录，并确认专用插件已启用）" }, 400).ConfigureAwait(false);
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { ok = true, profile }).ConfigureAwait(false);
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
        // v0.7.5（KN-27）：icon 响应补安全头（此前手工写响应漏 nosniff/CSP 等，与静态文件不一致）。
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

    private static void NormalizePaths(ScriptInstance script)
    {
        script.RootPath = StripPathQuotes(script.RootPath);
        script.MainExe = StripPathQuotes(script.MainExe);
        script.ConfigPath = StripPathQuotes(script.ConfigPath);
        script.LogPath = StripPathQuotes(script.LogPath);
        script.GameExe = StripPathQuotes(script.GameExe);
    }

    /// <summary>去除成对首尾引号（"…" / '…'），保留路径内部的合法单引号（如 C:\O'Brien\）。</summary>
    private static string StripPathQuotes(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
        string trimmed = value.Trim();
        if (trimmed.Length >= 2)
        {
            char first = trimmed[0];
            char last = trimmed[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                return trimmed[1..^1].Trim();
            }
        }
        return trimmed;
    }

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
                    await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                    return;
                }
                ScriptUser? user = HttpHelper.ParseBody<ScriptUser>(body);
                if (user is null || !ScriptUserRule.IsValidName(user.Name))
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = "用户名不能为空且不能包含非法字符" }, 400).ConfigureAwait(false);
                    return;
                }
                SemaphoreSlim gate = ScriptConfigGate.Get(seg[1]);
                if (!gate.Wait(0))
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = "脚本正在运行或编辑配置中，无法新增用户" }, 409).ConfigureAwait(false);
                    return;
                }
                try
                {
                // v0.7.2+（KN-04）：锁内完成「校验-读-写」整段，避免与并发请求/运行线程冲突；锁内不做 await。
                string? userLimit;
                lock (ctx.DataLock)
                {
                    userLimit = Limits.CheckUserCount(script.Users.Count);
                    if (userLimit is null
                        && script.Users.Any(existing => string.Equals(existing.Name, user.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        userLimit = "用户名重复：该脚本已存在同名用户";
                    }
                    if (userLimit is null)
                    {
                        script.Users.Add(user);
                        try
                        {
                            DataStore.SaveScripts(ctx.Scripts);
                        }
                        catch
                        {
                            script.Users.Remove(user);
                            throw;
                        }
                    }
                }
                if (userLimit is not null)
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = userLimit }, 400).ConfigureAwait(false);
                    return;
                }
                string? snapError = UserConfigManager.SnapshotOnAddUser(script, user.Name);
                if (snapError is not null)
                {
                    Logger.Warn($"[警告] 用户「{user.Name}」初始配置快照失败：{snapError}");
                    lock (ctx.DataLock)
                    {
                        script.Users.RemoveAll(existing => ReferenceEquals(existing, user));
                        DataStore.SaveScripts(ctx.Scripts);
                    }
                    await HttpHelper.WriteJsonAsync(context, new { error = "初始配置快照失败：" + snapError }, 400).ConfigureAwait(false);
                    return;
                }
                Audit.Log(Audit.Web, "添加用户", $"{script.Name} / {user.Name}");
                await HttpHelper.WriteJsonAsync(context, script).ConfigureAwait(false);
                return;
                }
                finally
                {
                    gate.Release();
                }
            }
            if (seg.Length == 4 && method == "PUT" && seg[3].Equals("order", StringComparison.OrdinalIgnoreCase))
            {
                await HandleReorderUsersAsync(context, seg, body).ConfigureAwait(false);
                return;
            }
            if (seg.Length == 4 && method == "PUT")
            {
                ScriptInstance? script = ctx.FindScript(seg[1]);
                string oldName = Uri.UnescapeDataString(seg[3]);
                if (script is null)
                {
                    await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                    return;
                }
                ScriptUser? existing = script.Users.FirstOrDefault(u => u.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                    return;
                }
                ScriptUser? update = HttpHelper.ParseBody<ScriptUser>(body);
                if (update is null || !ScriptUserRule.IsValidName(update.Name))
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = "用户名不能为空且不能包含非法字符" }, 400).ConfigureAwait(false);
                    return;
                }
                // v0.7.2+（KN-04）：锁内完成「查重-改名-保存」整段（改名仅指集合内对象字段更新，数据目录迁移在锁外做）；
                // 锁内不做 await。
                string? userError = null;
                lock (ctx.DataLock)
                {
                    if (!string.Equals(oldName, update.Name, StringComparison.OrdinalIgnoreCase)
                        && script.Users.Any(u => !ReferenceEquals(u, existing) && string.Equals(u.Name, update.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        userError = "用户名重复：该脚本已存在同名用户";
                    }
                }
                if (userError is not null)
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = userError }, 400).ConfigureAwait(false);
                    return;
                }
                if (!string.Equals(oldName, update.Name, StringComparison.OrdinalIgnoreCase))
                {
                    SemaphoreSlim gate = ScriptConfigGate.Get(seg[1]);
                    if (!gate.Wait(0))
                    {
                        await HttpHelper.WriteJsonAsync(context, new { error = "脚本正在运行或编辑配置中，无法编辑用户" }, 409).ConfigureAwait(false);
                        return;
                    }
                    try
                    {
                        string? renameError = UserConfigManager.RenameUserData(seg[1], oldName, update.Name);
                        if (renameError is not null)
                        {
                            await HttpHelper.WriteJsonAsync(context, new { error = renameError }, 400).ConfigureAwait(false);
                            return;
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                lock (ctx.DataLock)
                {
                    existing.Name = update.Name;
                    existing.Enabled = update.Enabled;
                    existing.PreRunScript = update.PreRunScript;
                    existing.PreRunOnceOnly = update.PreRunOnceOnly;
                    existing.PostRunScript = update.PostRunScript;
                    existing.PostRunOnFinalOnly = update.PostRunOnFinalOnly;
                    DataStore.SaveScripts(ctx.Scripts);
                }
                Audit.Log(Audit.Web, "编辑用户", $"{script.Name} / {oldName} → {existing.Name}");
                await HttpHelper.WriteJsonAsync(context, script).ConfigureAwait(false);
                return;
            }
            if (seg.Length == 4 && method == "DELETE")
            {
                ScriptInstance? script = ctx.FindScript(seg[1]);
                string userName = Uri.UnescapeDataString(seg[3]);
                if (script is null)
                {
                    await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                    return;
                }
                // v0.7.4（KN-37）：用户匹配统一 OrdinalIgnoreCase，与重名查重/顺序接口口径一致。
                if (script.Users.All(u => !u.Name.Equals(userName, StringComparison.OrdinalIgnoreCase)))
                {
                    await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
                    return;
                }
                SemaphoreSlim gate = ScriptConfigGate.Get(seg[1]);
                if (!gate.Wait(0))
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = "脚本正在运行或编辑配置中，无法删除用户" }, 409).ConfigureAwait(false);
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
                // v0.7.2+（KN-04）：锁内删除与保存，避免与并发请求/运行线程冲突。
                // v0.7.4（KN-37）：按名匹配忽略大小写，与上方存在性校验口径一致。
                lock (ctx.DataLock)
                {
                    script.Users.RemoveAll(u => u.Name.Equals(userName, StringComparison.OrdinalIgnoreCase));
                    DataStore.SaveScripts(ctx.Scripts);
                }
                Audit.Log(Audit.Web, "删除用户", $"{script.Name} / {userName}");
                await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
                return;
            }
            if (seg.Length == 5 && method == "POST" && seg[4].Equals("edit-config", StringComparison.OrdinalIgnoreCase))
            {
                await HandleEditConfigAsync(context, seg, body).ConfigureAwait(false);
                return;
            }
        }
        await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    /// <summary>脚本实例顺序重排（v0.6.8+）：请求体携带完整 id 名单，与现有集合完全一致时按新顺序重赋 Index 落盘。</summary>
    private static async Task HandleReorderScriptsAsync(HttpListenerContext context, string body)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        JsonNode? node = HttpHelper.ParseBody(body);
        List<string>? ids = node?["ids"] is JsonArray array
            ? array.Select(item => item?.ToString() ?? "").ToList()
            : null;
        // v0.7.2+（KN-04）：锁内完成「校验-重排-保存」整段，避免与并发请求冲突；锁内不做 await，结果在锁外响应。
        string? error = null;
        lock (ctx.DataLock)
        {
            if (ids is null || ids.Count != ctx.Scripts.Count
                || ids.Any(string.IsNullOrWhiteSpace)
                || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            {
                error = "脚本顺序名单缺失或与当前脚本列表不一致";
            }
            else
            {
                HashSet<string> existing = new(ctx.Scripts.Select(script => script.Id), StringComparer.Ordinal);
                if (ids.Any(id => !existing.Contains(id)))
                {
                    error = "脚本顺序名单与当前脚本列表不一致";
                }
                else
                {
                    Dictionary<string, ScriptInstance> byId = ctx.Scripts.ToDictionary(script => script.Id, StringComparer.Ordinal);
                    for (int i = 0; i < ids.Count; i++)
                    {
                        byId[ids[i]].Index = i;
                    }
                    DataStore.SaveScripts(ctx.Scripts);
                }
            }
        }
        if (error is not null)
        {
            await HttpHelper.WriteJsonAsync(context, new { error }, 400).ConfigureAwait(false);
            return;
        }
        Audit.Log(Audit.Web, "调整脚本顺序", $"{ids!.Count} 个脚本实例");
        await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
    }

    /// <summary>用户顺序重排：请求体携带完整用户名名单（忽略大小写），与现有用户集合完全一致时按新顺序落盘。</summary>
    private static async Task HandleReorderUsersAsync(HttpListenerContext context, string[] seg, string body)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        ScriptInstance? script = ctx.FindScript(seg[1]);
        if (script is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        JsonNode? node = HttpHelper.ParseBody(body);
        List<string>? names = node?["names"] is JsonArray array
            ? array.Select(item => item?.ToString() ?? "").ToList()
            : null;
        SemaphoreSlim gate = ScriptConfigGate.Get(seg[1]);
        if (!gate.Wait(0))
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "脚本正在运行或编辑配置中，无法调整用户顺序" }, 409).ConfigureAwait(false);
            return;
        }
        try
        {
            // v0.7.2+（KN-04）：锁内完成「重排-保存」整段，避免与并发请求/运行线程冲突；锁内不做 await。
            string? reorderError = null;
            lock (ctx.DataLock)
            {
                if (names is null || names.Count != script.Users.Count
                    || names.Any(string.IsNullOrWhiteSpace)
                    || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
                {
                    reorderError = "用户顺序名单缺失或与当前用户列表不一致";
                }
                else
                {
                    HashSet<string> existing = new(script.Users.Select(user => user.Name), StringComparer.OrdinalIgnoreCase);
                    if (names.Any(name => !existing.Contains(name)))
                    {
                        reorderError = "用户顺序名单与当前用户列表不一致";
                    }
                    else
                    {
                        Dictionary<string, ScriptUser> byName = script.Users.ToDictionary(user => user.Name, StringComparer.OrdinalIgnoreCase);
                        script.Users.Clear();
                        foreach (string name in names)
                        {
                            script.Users.Add(byName[name]);
                        }
                        DataStore.SaveScripts(ctx.Scripts);
                    }
                }
            }
            if (reorderError is not null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = reorderError }, 400).ConfigureAwait(false);
                return;
            }
            Audit.Log(Audit.Web, "调整用户顺序", $"{script.Name} / {names!.Count} 个用户");
            await HttpHelper.WriteJsonAsync(context, script).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task HandleEditConfigAsync(HttpListenerContext context, string[] seg, string body)
    {
        RuntimeContext ctx = RuntimeContext.Instance;
        string scriptId = seg[1];
        string userName = Uri.UnescapeDataString(seg[3]);
        string action = HttpHelper.ParseBody(body).Get("action").Str();
        ScriptInstance? script = ctx.FindScript(scriptId);
        if (script is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        ScriptUser? user;
        lock (ctx.DataLock)
        {
            // v0.7.2+（KN-04）：锁内枚举用户集合，避免与并发修改冲突。
            user = script.Users.FirstOrDefault(u => u.Name == userName);
        }
        if (user is null)
        {
            await HttpHelper.NotFoundAsync(context).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(script.ConfigPath))
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "脚本未配置「配置文件路径/文件夹」" }, 400).ConfigureAwait(false);
            return;
        }
        SemaphoreSlim gate = ScriptConfigGate.Get(scriptId);
        if (action == "start")
        {
            if (!gate.Wait(0))
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "脚本正在运行或编辑配置中" }, 409).ConfigureAwait(false);
                return;
            }
            bool keepGate = false;
            try
            {
                if (!TextRules.IsExecutable(script.MainExe))
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = "脚本主程序路径错误或不是可执行文件" }, 400).ConfigureAwait(false);
                    return;
                }
                if (SystemActions.IsExeRunning(script.MainExe)
                    || SystemActions.IsExeRunning(ResolveLaunchTargetExe(script)))
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = "检测到已打开的脚本，退出脚本后才能编辑配置。" }, 409).ConfigureAwait(false);
                    return;
                }
                UserConfigManager.RestoreHiddenConfigs(script.Id, user.Name, script.ConfigPath);
                string? prepError = UserConfigManager.PrepareForEdit(script.Id, user.Name, script.ConfigPath);
                if (prepError is not null)
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = "配置交换失败：" + prepError }, 400).ConfigureAwait(false);
                    return;
                }
                List<string> generatedTemplateFiles = UserConfigManager.EnsureConfigForEdit(script);
                bool generatedTemplate = generatedTemplateFiles.Count > 0;
                // v0.7.5（台账外）：模板生成后立即持久化标记（GeneratedTemplate/TemplateFiles）——此前补写发生在
                // 主程序启动之后（StartVisible 失败路径的 CancelEdit 与崩溃窗口内标记仍为 PrepareForRun 的无模板版本，
                // 文件型 config 的模板兄弟文件无清单记录永久残留）。
                var editMark = new ConfigSessionMark
                {
                    ScriptId = script.Id,
                    UserName = user.Name,
                    ConfigPath = script.ConfigPath,
                    OriginalKind = ConfigSessionMark.TryRead(script.Id, user.Name)?.OriginalKind
                        ?? PathKindUtil.Text(PathKindUtil.KindOf(script.ConfigPath)),
                    Phase = "edit",
                    GeneratedTemplate = generatedTemplate,
                    TemplateFiles = generatedTemplateFiles,
                };
                editMark.Write();
                UserConfigManager.HideOtherConfigs(script, script.Id, user.Name);
                Process? proc;                try
                {
                    proc = SystemActions.StartVisible(script.MainExe,
                        string.IsNullOrWhiteSpace(script.RootPath) ? Path.GetDirectoryName(script.MainExe) ?? "" : script.RootPath);
                }
                catch (Exception ex)
                {
                    UserConfigManager.CancelEdit(script.Id, user.Name, script.ConfigPath);
                    await HttpHelper.WriteJsonAsync(context, new { error = "主程序启动失败：" + ex.Message + "，配置已还原，可修正后重试" }, 400).ConfigureAwait(false);
                    return;
                }
                // v0.6.5+：编辑配置场景主程序窗口前置（用户需在窗口内编辑配置），避免被浏览器等前台窗口遮挡。
                SystemActions.BringToFrontFireAndForget(proc?.Id ?? 0, "编辑配置");
                var editSession = new EditSession
                {
                    Script = script,
                    User = user,
                    Process = proc,
                    GeneratedConfigTemplate = generatedTemplate,
                    Mark = editMark,
                };
                UserConfigManager.EditSessions[scriptId] = editSession;
                keepGate = true;
                Audit.Log(Audit.Web, "开始编辑配置", $"{script.Name} / {user.Name}（主程序已启动）");
                await HttpHelper.WriteJsonAsync(context, new { ok = true, pid = proc?.Id ?? 0 }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (keepGate)
                {
                    // v0.7.2+（KN-42）：会话已注册（keepGate=true）后发生异常（如响应写入失败/客户端断开）时
                    // 主动清理现场——结束已启动的编辑进程、还原配置交换与隐藏配置、移除会话并释放门禁，
                    // 避免门禁永久占住脚本直到重启；清理失败保留标记交由自愈/后台重试兜底。
                    try
                    {
                        if (UserConfigManager.EditSessions.TryGetValue(scriptId, out EditSession? registered))
                        {
                            if (registered.Process is not null)
                            {
                                SystemActions.KillAndConfirmExited(registered.Process.Id, ResolveLaunchTargetExe(script), "脚本");
                            }
                            UserConfigManager.CancelEdit(script.Id, user.Name, script.ConfigPath);
                            UserConfigManager.RestoreHiddenConfigs(script.Id, user.Name, script.ConfigPath);
                            UserConfigManager.EditSessions.TryRemove(scriptId, out _);
                        }
                    }
                    catch (Exception cleanupEx)
                    {
                        Logger.Error($"[错误] 编辑配置会话异常后的现场清理失败（交由自愈兜底）：{cleanupEx.Message}");
                    }
                    keepGate = false;
                }
                await HttpHelper.WriteJsonAsync(context, new { error = ex.Message }, 400).ConfigureAwait(false);
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
            if (!UserConfigManager.EditSessions.TryGetValue(scriptId, out EditSession? session))
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "没有进行中的编辑配置会话" }, 409).ConfigureAwait(false);
                return;
            }
            bool sessionRemoved = false;
            try
            {
                // v0.6.6+：done/cancel 自动结束脚本进程并确认退出（按启动目标名轮询强杀，处理防崩溃自重启脚本），
                // 确保配置交换还原前进程已完全退出，消除文件占用导致的交换失败窗口；
                // 持续自重启杀不干净时拒绝执行文件交换（会话与标记保留，由下次重试自愈）。
                string launchExe = ResolveLaunchTargetExe(session.Script);
                if (!SystemActions.KillAndConfirmExited(session.Process?.Id ?? 0, launchExe, "脚本"))
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = "脚本程序无法完全退出（可能持续自重启），请先在托盘退出脚本后重试" }, 400).ConfigureAwait(false);
                    return;
                }
                string? swapError = action == "done"
                    ? UserConfigManager.CommitEdit(scriptId, user.Name, script.ConfigPath)
                    : UserConfigManager.CancelEdit(scriptId, user.Name, script.ConfigPath);
                if (swapError is not null)
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = (action == "done" ? "提交" : "取消") + "失败：" + swapError }, 400).ConfigureAwait(false);
                    return;
                }
                if (action == "cancel" && session.GeneratedConfigTemplate)
                {
                    DeleteGeneratedTemplateFiles(session.Mark);
                }
                UserConfigManager.RestoreHiddenConfigs(scriptId, user.Name, script.ConfigPath);
                // 文件交换成功后才移除会话（失败保留，可原地重试；.session 标记由自愈/后台重试兜底）。
                sessionRemoved = UserConfigManager.EditSessions.TryRemove(scriptId, out _);
                Audit.Log(Audit.Web, action == "done" ? "完成编辑配置" : "取消编辑配置", $"{script.Name} / {user.Name}");
                await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = ex.Message }, 400).ConfigureAwait(false);
            }
            finally
            {
                // v0.6.10 修复：仅会话成功移除（提交/取消成功）才释放门禁——失败路径会话保留（可原地重试），
                // 门禁随之保持；此前无条件 Release 导致重试成功路径 finally 二次 Release，
                // SemaphoreSlim(1,1) 溢出「Adding the specified count...」（CI 曾现）。
                if (sessionRemoved)
                {
                    gate.Release();
                }
            }
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { error = "未知操作：" + action }, 400).ConfigureAwait(false);
    }

    /// <summary>按会话标记的模板文件清单清理编辑会话生成的模板（v0.6.3+ 模板目录形态；无清单回退清理 ConfigPath 单文件）。</summary>
    private static void DeleteGeneratedTemplateFiles(ConfigSessionMark mark)
    {
        if (mark.TemplateFiles.Count > 0)
        {
            string? baseDir = Path.GetDirectoryName(mark.ConfigPath);
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                return;
            }
            foreach (string rel in mark.TemplateFiles)
            {
                try
                {
                    string dest = Path.Combine(baseDir, rel);
                    if (File.Exists(dest))
                    {
                        File.Delete(dest);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[警告] 清理编辑会话生成的配置模板失败：{rel}（{ex.Message}）");
                }
            }
            return;
        }
        try
        {
            if (File.Exists(mark.ConfigPath))
            {
                File.Delete(mark.ConfigPath);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 清理编辑会话生成的配置模板失败：{ex.Message}");
        }
    }
}
