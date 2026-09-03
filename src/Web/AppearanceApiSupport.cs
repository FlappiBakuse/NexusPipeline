using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.Services;

namespace NexusPipeline.Web;

/// <summary>外观 API 的共享 HTTP 投影与调用方解析支持。</summary>
internal static class AppearanceApiSupport
{
    internal static async Task WriteSnapshotAsync(HttpListenerContext context, AppearanceSnapshot snapshot)
    {
        await HttpHelper.WriteJsonAsync(context, new
        {
            ok = true,
            schemaVersion = snapshot.SchemaVersion,
            revision = snapshot.Revision,
            effectiveEnabled = snapshot.EffectiveEnabled,
            provider = new
            {
                pluginName = snapshot.Provider.PluginName,
                enabled = snapshot.Provider.Enabled,
            },
            assets = snapshot.Assets.Select(ToAssetDto).ToArray(),
            order = snapshot.Order,
            selectedId = snapshot.SelectedId,
            currentId = snapshot.CurrentId,
            rotation = new
            {
                mode = snapshot.Rotation.Mode,
                intervalMinutes = snapshot.Rotation.IntervalMinutes,
                epochUnixMs = snapshot.Rotation.EpochUnixMs,
            },
            effects = new
            {
                blurPx = snapshot.Effects.BlurPx,
                dimPercent = snapshot.Effects.DimPercent,
                surfaceTransparencyPercent = snapshot.Effects.SurfaceTransparencyPercent,
                applyTransparencyToSecondarySurfaces = snapshot.Effects.ApplyTransparencyToSecondarySurfaces,
            },
            nextSwitchAt = snapshot.NextSwitchAt?.ToString("O"),
        }).ConfigureAwait(false);
    }

    internal static object ToAssetDto(AppearanceAsset asset) => new
    {
        id = asset.Id,
        originalName = asset.OriginalName,
        mimeType = asset.MimeType,
        sizeBytes = asset.SizeBytes,
        sha256 = asset.Sha256,
        createdAt = asset.CreatedAt.ToString("O"),
        paletteVersion = asset.PaletteVersion,
        palette = asset.Palette,
        url = "/api/appearance-assets/" + Uri.EscapeDataString(asset.Id),
    };

    internal static string ResolveCaller(HttpListenerContext context, JsonObject? body)
    {
        string caller = body?["provider"]?["pluginName"]?.ToString()?.Trim() ?? "";
        if (caller.Length == 0)
        {
            caller = context.Request.QueryString["plugin"]?.Trim() ?? "";
        }
        if (caller.Length == 0)
        {
            caller = context.Request.Headers["X-Nexus-Plugin"]?.Trim() ?? "";
        }
        return caller;
    }

    internal static int StatusCode(string code) => code switch
    {
        "forbidden" => 403,
        "not_found" => 404,
        "too_large" or "quota" => 413,
        "internal_error" => 500,
        _ => 400,
    };
}
