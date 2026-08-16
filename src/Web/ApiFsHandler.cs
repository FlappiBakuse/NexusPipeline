using System.Net;
using NexusPipeline.Models;

namespace NexusPipeline.Web;

[ApiRoute("fs")]
internal static class ApiFsHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg)
    {
        if (seg.Length < 2)
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        if (seg[1].Equals("browse", StringComparison.OrdinalIgnoreCase) && method == "GET")
        {
            await HandleBrowseAsync(context).ConfigureAwait(false);
            return;
        }
        await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    private static async Task HandleBrowseAsync(HttpListenerContext context)
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
                await HttpHelper.WriteJsonAsync(context, new { path = "", parent = (string?)null, dirs = drives, files = Array.Empty<string>() }).ConfigureAwait(false);
                return;
            }
            if (!Directory.Exists(path))
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "目录不存在：" + path }, 400).ConfigureAwait(false);
                return;
            }
            if (!IsWhitelisted(path))
            {
                await HttpHelper.WriteJsonAsync(context, new { error = "路径不在允许浏览范围内（仅限已配置脚本的根目录/配置路径/游戏路径及其子路径）" }, 403).ConfigureAwait(false);
                return;
            }
            var dirs = Directory.EnumerateDirectories(path).OrderBy(d => d).ToList();
            var files = Directory.EnumerateFiles(path).OrderBy(f => f).ToList();
            string? parent = Directory.GetParent(path)?.FullName;
            await HttpHelper.WriteJsonAsync(context, new { path, parent, dirs, files }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await HttpHelper.WriteJsonAsync(context, new { error = "读取目录失败：" + ex.Message }, 400).ConfigureAwait(false);
        }
    }

    /// <summary>浏览白名单（v0.6.4+）：仅允许已配置脚本的根目录、配置路径、游戏路径及其子路径，防止经 API 遍历任意磁盘。</summary>
    private static bool IsWhitelisted(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        }
        catch
        {
            return false;
        }
        // v0.7.2+（KN-04）：快照后遍历，避免与并发修改冲突。
        foreach (ScriptInstance script in RuntimeContext.Instance.SnapshotScripts())
        {
            foreach (string prefix in AllowedPrefixes(script))
            {
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static IEnumerable<string> AllowedPrefixes(ScriptInstance script)
    {
        if (!string.IsNullOrWhiteSpace(script.RootPath))
        {
            yield return PathPrefix(script.RootPath);
        }
        if (!string.IsNullOrWhiteSpace(script.ConfigPath))
        {
            yield return PathPrefix(script.ConfigPath);
            string? configDir = Path.GetDirectoryName(script.ConfigPath);
            if (!string.IsNullOrWhiteSpace(configDir))
            {
                yield return PathPrefix(configDir);
            }
        }
        if (!string.IsNullOrWhiteSpace(script.GameExe))
        {
            string? gameDir = Path.GetDirectoryName(script.GameExe);
            if (!string.IsNullOrWhiteSpace(gameDir))
            {
                yield return PathPrefix(gameDir);
            }
        }
    }

    private static string PathPrefix(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        }
        catch
        {
            return path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        }
    }
}
