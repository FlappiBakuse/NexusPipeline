using System.Net;
using System.Text.Json;
using NexusPipeline.Modules.Dashboard;
using NexusPipeline.Shared.Localization;

namespace NexusPipeline.ControlPlane.Http;

[ApiRoute("dashboard", MaxBodyBytes = 256 * 1024)]
internal static class ApiDashboardHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body, DashboardService dashboard)
    {
        if (seg.Length != 2 || seg[1] is not ("layout" or "cards")) { await HttpHelper.NotFoundAsync(context).ConfigureAwait(false); return; }
        if (method != "GET" && (method != "PUT" || seg[1] != "layout")) { await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false); return; }
        string locale = LocaleCatalog.Resolve(context.Request.Headers["Accept-Language"] is null ? context.Request.Headers["X-Nexus-Locale"] : null,
            context.Request.Headers["Accept-Language"]);
        context.Response.Headers["Content-Language"] = locale;
        context.Response.Headers["Vary"] = "Accept-Language";
        context.Response.Headers["Cache-Control"] = "no-store";
        try
        {
            if (method == "GET")
            {
                var snapshot = dashboard.Read(locale);
                if (seg[1] == "cards")
                    await HttpHelper.WriteBinaryAsync(context, JsonSerializer.SerializeToUtf8Bytes(new DashboardCatalog(1, snapshot.CatalogRevision, snapshot.Cards), DashboardRepresentation.Json), "application/json; charset=utf-8").ConfigureAwait(false);
                else
                {
                    var representation = DashboardRepresentation.Create(snapshot);
                    await HttpHelper.WriteBinaryAsync(context, representation.Body, "application/json; charset=utf-8", new Dictionary<string, string> { ["ETag"] = representation.ETag }).ConfigureAwait(false);
                }
                return;
            }
            var request = JsonSerializer.Deserialize<DashboardWriteRequest>(body, DashboardRepresentation.Json) ?? throw new DashboardFailure(DashboardFailureKind.Invalid);
            var saved = dashboard.Save(locale, context.Request.Headers["If-Match"], request);
            await HttpHelper.WriteBinaryAsync(context, DashboardRepresentation.Create(saved).Body, "application/json; charset=utf-8").ConfigureAwait(false);
        }
        catch (JsonException) { await Error(context, DashboardFailureKind.Invalid).ConfigureAwait(false); }
        catch (DashboardFailure e) { await Error(context, e.Kind).ConfigureAwait(false); }
    }

    private static Task Error(HttpListenerContext context, DashboardFailureKind kind)
    {
        var (status, code) = kind switch
        {
            DashboardFailureKind.PreconditionRequired => (428, "dashboard_layout_precondition_required"),
            DashboardFailureKind.PreconditionInvalid => (400, "dashboard_layout_precondition_invalid"),
            DashboardFailureKind.CatalogChanged => (409, "dashboard_catalog_changed"),
            DashboardFailureKind.Conflict => (412, "dashboard_layout_conflict"),
            DashboardFailureKind.CardInvalid => (400, "dashboard_layout_card_invalid"),
            DashboardFailureKind.TooLarge => (413, "dashboard_layout_too_large"),
            DashboardFailureKind.PersistenceFailed => (503, "dashboard_layout_persistence_failed"),
            DashboardFailureKind.Unavailable => (503, "dashboard_layout_unavailable"),
            _ => (400, "dashboard_layout_invalid")
        };
        return HttpHelper.WriteJsonAsync(context, new { code }, status, cacheControl: "no-store");
    }
}
