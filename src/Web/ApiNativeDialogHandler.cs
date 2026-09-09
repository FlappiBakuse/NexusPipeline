using NexusPipeline.Services;
using NexusPipeline.Utilities;

namespace NexusPipeline.Web;

/// <summary>本机路径选择器：仅允许回环请求，实际对话框由 WinForms STA 线程承载。</summary>
[ApiRoute("native-dialog")]
internal static class ApiNativeDialogHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        if (method != "POST" || seg.Length != 1)
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }
        if (!HttpHelper.IsLoopback(context))
        {
            await HttpHelper.ErrorAsync(context, "local_only", 403).ConfigureAwait(false);
            return;
        }

        NativePathPickerPayload? payload = HttpHelper.ParseBody<NativePathPickerPayload>(body);
        string kind = (payload?.Kind ?? "file").Trim().ToLowerInvariant();
        if (kind is not ("file" or "folder"))
        {
            await HttpHelper.ErrorAsync(context, "invalid_request", 400).ConfigureAwait(false);
            return;
        }
        string title = string.IsNullOrWhiteSpace(payload?.Title)
            ? kind == "folder" ? "选择文件夹" : "选择文件"
            : payload.Title.Trim();
        string initialPath = payload?.InitialPath?.Trim() ?? "";
        string filter = payload?.Filter?.Trim() ?? "";
        string invalidInitialPathMessage = payload?.InvalidInitialPathMessage?.Trim() ?? "";
        if (title.Length > 128 || initialPath.Length > 4096 || filter.Length > 1024 || invalidInitialPathMessage.Length > 128)
        {
            await HttpHelper.ErrorAsync(context, "invalid_request", 400).ConfigureAwait(false);
            return;
        }
        if (payload?.RequireInitialDirectory == true && !NativePathPickerService.IsExistingDirectory(initialPath))
        {
            await HttpHelper.ErrorAsync(context, "initial_directory_not_found", 400).ConfigureAwait(false);
            return;
        }

        try
        {
            string? selected = await RuntimeContext.Instance.Resolve<NativePathPickerService>()
                .PickAsync(new NativePathPickerRequest(kind, title, initialPath, filter))
                .ConfigureAwait(false);
            await HttpHelper.WriteJsonAsync(context, new
            {
                ok = true,
                cancelled = selected is null,
                path = selected ?? "",
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[路径选择器] 打开原生选择器失败：{ex.Message}");
            await HttpHelper.ErrorAsync(context, "dialog_failed", 500).ConfigureAwait(false);
        }
    }

    internal sealed class NativePathPickerPayload
    {
        public string? Kind { get; set; }

        public string? Title { get; set; }

        public string? InitialPath { get; set; }

        public string? Filter { get; set; }

        public bool RequireInitialDirectory { get; set; }

        public string? InvalidInitialPathMessage { get; set; }
    }
}
