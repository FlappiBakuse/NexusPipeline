using System.Net;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.ControlPlane.Http;

[ApiRoute("fs")]
internal static class ApiFsHandler
{
    public static async Task Handle(
        HttpListenerContext context,
        string method,
        string[] seg,
        ScriptFileBrowser fileBrowser)
    {
        if (seg.Length < 2)
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        if (seg[1].Equals("browse", StringComparison.OrdinalIgnoreCase) && method == "GET")
        {
            await HandleBrowseAsync(context, fileBrowser).ConfigureAwait(false);
            return;
        }
        await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
    }

    private static async Task HandleBrowseAsync(HttpListenerContext context, ScriptFileBrowser fileBrowser)
    {
        string? path = context.Request.QueryString["path"];
        ScriptFileBrowseResult result = fileBrowser.Browse(path);
        if (!result.Succeeded)
        {
            int status = result.ErrorCode == "fs_path_forbidden" ? 403 : 400;
            await HttpHelper.ErrorAsync(context, result.ErrorCode ?? "fs_read_failed", status,
                result.ErrorCode == "fs_path_not_found" ? new { path } : null).ConfigureAwait(false);
            return;
        }
        FileBrowserResult data = result.Data!;
        await HttpHelper.WriteJsonAsync(context, new
        {
            path = data.Path,
            parent = data.Parent,
            dirs = data.Directories,
            files = data.Files,
        }).ConfigureAwait(false);
    }
}
