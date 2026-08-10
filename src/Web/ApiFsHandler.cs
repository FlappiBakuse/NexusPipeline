using System.Net;

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
}
