using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.Services;

namespace NexusPipeline.Web;

[ApiRoute("appearance")]
internal static class ApiAppearanceHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body)
    {
        AppearanceService service = RuntimeContext.Instance.Resolve<AppearanceService>();
        try
        {
            if (method == "GET" && seg.Length == 1)
            {
                await WriteSnapshotAsync(context, service.GetSnapshot()).ConfigureAwait(false);
                return;
            }
            if (method == "PUT" && seg.Length == 1)
            {
                JsonNode? node = HttpHelper.ParseBody(body);
                if (node is not JsonObject patch)
                {
                    await HttpHelper.WriteJsonAsync(context, new { ok = false, code = "invalid_config", error = "外观配置请求体无效" }, 400).ConfigureAwait(false);
                    return;
                }
                string caller = ResolveCaller(context, patch);
                await WriteSnapshotAsync(context, service.Save(patch, caller)).ConfigureAwait(false);
                return;
            }
            if (method == "POST" && seg.Length == 2 && seg[1].Equals("rotation", StringComparison.OrdinalIgnoreCase))
            {
                await HttpHelper.WriteJsonAsync(context, new { ok = false, code = "invalid_action", error = "外观轮换操作无效" }, 400).ConfigureAwait(false);
                return;
            }
            if (method == "POST" && seg.Length == 3
                && seg[1].Equals("rotation", StringComparison.OrdinalIgnoreCase)
                && seg[2].Equals("startup", StringComparison.OrdinalIgnoreCase))
            {
                string caller = ResolveCaller(context, null);
                await WriteSnapshotAsync(context, service.StartStartupRotation(caller)).ConfigureAwait(false);
                return;
            }
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
        }
        catch (AppearanceException ex)
        {
            await HttpHelper.WriteJsonAsync(context, new { ok = false, code = ex.Code, error = ex.Message }, StatusCode(ex.Code)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await HttpHelper.WriteJsonAsync(context, new { ok = false, code = "internal_error", error = ex.Message }, 500).ConfigureAwait(false);
        }
    }

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
