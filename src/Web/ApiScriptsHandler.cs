using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using NexusPipeline.Plugins;

namespace NexusPipeline.Web;

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
            await HttpHelper.WriteJsonAsync(context, ctx.Scripts).ConfigureAwait(false);
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
            string? limitError = Limits.CheckScriptCount(ctx.Scripts.Count)
                ?? Limits.CheckNameBytes(script.Name, Limits.Current.MaxScriptNameBytes, "脚本名称")
                ?? Limits.CheckAttempts(script.MaxAttempts)
                ?? Limits.CheckStallMinutes(script.LogStallTimeoutMinutes)
                ?? Limits.CheckTotalMinutes(script.TotalTimeoutMinutes);
            if (limitError is not null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = limitError }, 400).ConfigureAwait(false);
                return;
            }
            if (string.IsNullOrWhiteSpace(script.Id) || ctx.FindScript(script.Id) is null)
            {
                script.Id = Guid.NewGuid().ToString("N");
            }
            NormalizePaths(script);
            string? pluginError = string.IsNullOrWhiteSpace(script.PluginType) ? null : ApplyProfile(script);
            if (pluginError is not null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = pluginError }, 400).ConfigureAwait(false);
                return;
            }
            string? pathError = Limits.CheckScriptPaths(script);
            if (pathError is not null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = pathError }, 400).ConfigureAwait(false);
                return;
            }
            ctx.Scripts.Add(script);
            DataStore.SaveScripts(ctx.Scripts);
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
            string? limitError = Limits.CheckNameBytes(update.Name, Limits.Current.MaxScriptNameBytes, "脚本名称")
                ?? Limits.CheckAttempts(update.MaxAttempts)
                ?? Limits.CheckStallMinutes(update.LogStallTimeoutMinutes)
                ?? Limits.CheckTotalMinutes(update.TotalTimeoutMinutes);
            if (limitError is not null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = limitError }, 400).ConfigureAwait(false);
                return;
            }
            update.Id = existing.Id;
            update.Users = existing.Users;
            NormalizePaths(update);
            string? pluginError = string.IsNullOrWhiteSpace(update.PluginType) ? null : ApplyProfile(update);
            if (pluginError is not null)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = pluginError }, 400).ConfigureAwait(false);
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
            int index = ctx.Scripts.IndexOf(existing);
            ctx.Scripts[index] = update;
            DataStore.SaveScripts(ctx.Scripts);
            Audit.Log(Audit.Web, "修改脚本实例", $"{update.Name}（id={update.Id}）");
            await HttpHelper.WriteJsonAsync(context, update).ConfigureAwait(false);
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
            ctx.Scripts.RemoveAll(script => script.Id == seg[1]);
            lock (IconSync)
            {
                IconCache.Remove(seg[1]);
            }
            DataStore.SaveScripts(ctx.Scripts);
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
        script.SuccessMarkers = profile.SuccessMarkers;
        script.SuccessKeywords = "";
        script.FailureKeywords = "";
        script.JudgeScriptEnabled = false;
        script.JudgeScriptLanguage = "";
        script.JudgeScript = "";
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
                string? userLimit = Limits.CheckUserCount(script.Users.Count);
                if (userLimit is not null)
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = userLimit }, 400).ConfigureAwait(false);
                    return;
                }
                if (script.Users.Any(existing => string.Equals(existing.Name, user.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = "用户名重复：该脚本已存在同名用户" }, 400).ConfigureAwait(false);
                    return;
                }
                script.Users.Add(user);
                DataStore.SaveScripts(ctx.Scripts);
                string? snapError = UserConfigManager.SnapshotOnAddUser(script, user.Name);
                Audit.Log(Audit.Web, "添加用户", $"{script.Name} / {user.Name}");
                if (snapError is not null)
                {
                    Logger.Warn($"[警告] 用户「{user.Name}」初始配置快照失败：{snapError}");
                }
                await HttpHelper.WriteJsonAsync(context, script).ConfigureAwait(false);
                return;
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
                ScriptUser? existing = script.Users.FirstOrDefault(u => u.Name == oldName);
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
                if (!string.Equals(oldName, update.Name, StringComparison.OrdinalIgnoreCase)
                    && script.Users.Any(u => !ReferenceEquals(u, existing) && string.Equals(u.Name, update.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = "用户名重复：该脚本已存在同名用户" }, 400).ConfigureAwait(false);
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
                existing.Name = update.Name;
                existing.Enabled = update.Enabled;
                existing.PreRunScript = update.PreRunScript;
                existing.PreRunOnceOnly = update.PreRunOnceOnly;
                existing.PostRunScript = update.PostRunScript;
                existing.PostRunOnFinalOnly = update.PostRunOnFinalOnly;
                DataStore.SaveScripts(ctx.Scripts);
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
                if (script.Users.All(u => u.Name != userName))
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
                script.Users.RemoveAll(u => u.Name == userName);
                DataStore.SaveScripts(ctx.Scripts);
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
        if (names is null || names.Count != script.Users.Count
            || names.Any(string.IsNullOrWhiteSpace)
            || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "用户顺序名单缺失或与当前用户列表不一致" }, 400).ConfigureAwait(false);
            return;
        }
        HashSet<string> existing = new(script.Users.Select(user => user.Name), StringComparer.OrdinalIgnoreCase);
        if (names.Any(name => !existing.Contains(name)))
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "用户顺序名单与当前用户列表不一致" }, 400).ConfigureAwait(false);
            return;
        }
        SemaphoreSlim gate = ScriptConfigGate.Get(seg[1]);
        if (!gate.Wait(0))
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "脚本正在运行或编辑配置中，无法调整用户顺序" }, 409).ConfigureAwait(false);
            return;
        }
        try
        {
            Dictionary<string, ScriptUser> byName = script.Users.ToDictionary(user => user.Name, StringComparer.OrdinalIgnoreCase);
            script.Users.Clear();
            foreach (string name in names)
            {
                script.Users.Add(byName[name]);
            }
            DataStore.SaveScripts(ctx.Scripts);
            Audit.Log(Audit.Web, "调整用户顺序", $"{script.Name} / {names.Count} 个用户");
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
        ScriptUser? user = script.Users.FirstOrDefault(u => u.Name == userName);
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
                string? prepError = UserConfigManager.PrepareForEdit(script.Id, user.Name, script.ConfigPath);
                if (prepError is not null)
                {
                    await HttpHelper.WriteJsonAsync(context, new { error = "配置交换失败：" + prepError }, 400).ConfigureAwait(false);
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
                    await HttpHelper.WriteJsonAsync(context, new { error = "主程序启动失败：" + ex.Message + "，配置已还原，可修正后重试" }, 400).ConfigureAwait(false);
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
                await HttpHelper.WriteJsonAsync(context, new { ok = true, pid = proc?.Id ?? 0 }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
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
            if (!UserConfigManager.EditSessions.TryRemove(scriptId, out EditSession? session))
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "没有进行中的编辑配置会话" }, 409).ConfigureAwait(false);
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
                    await HttpHelper.WriteJsonAsync(context, new { error = (action == "done" ? "提交" : "取消") + "失败：" + swapError }, 400).ConfigureAwait(false);
                    return;
                }
                Audit.Log(Audit.Web, action == "done" ? "完成编辑配置" : "取消编辑配置", $"{script.Name} / {user.Name}");
                await HttpHelper.WriteJsonAsync(context, new { ok = true }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await HttpHelper.WriteJsonAsync(context, new { error = ex.Message }, 400).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
            return;
        }
        await HttpHelper.WriteJsonAsync(context, new { error = "未知操作：" + action }, 400).ConfigureAwait(false);
    }
}
