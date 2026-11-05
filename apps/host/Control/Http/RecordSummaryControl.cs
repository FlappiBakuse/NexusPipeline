using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Records;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Records;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class RecordSummaryControl(ControlAuthentication authority, RecordSummaryReader records, RecordDisplayReader display)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/records/{runId}/display", (HttpContext context, string runId) =>
        {
            authority.Require(context, ControlCapability.RecordsRead);
            new ControlQueryValues(context.Request.Query).Validate();
            var value = display.Read(ControlQueryValues.Id(runId));
            return value is null ? ControlErrors.Result(404, "record_display_unavailable") : Results.Json(value, NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/records/summary", (HttpContext context) =>
        {
            authority.Require(context, ControlCapability.RecordsRead);
            var query = new ControlQueryValues(context.Request.Query, "endedFromInclusive", "endedUntilExclusive",
                "offsetMinutes", "userId", "business", "afterUserId", "lifecycle"); query.Validate();
            return Results.Json(records.Read(new RecordSummaryQuery(
                query.Utc("endedFromInclusive") ?? throw new ControlRequestException(400, "query_invalid"),
                query.Utc("endedUntilExclusive") ?? throw new ControlRequestException(400, "query_invalid"),
                query.Integer("offsetMinutes", 0), query.OptionalId("userId"), query.Business("business"),
                query.OptionalId("afterUserId"), query.Lifecycle("lifecycle"))), NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/records/{runId}", (HttpContext context, string runId) =>
        {
            authority.Require(context, ControlCapability.RecordsRead);
            var query = new ControlQueryValues(context.Request.Query, "userId"); query.Validate();
            var record = records.Read(ControlQueryValues.Id(runId), query.OptionalId("userId"));
            return record is null ? ControlErrors.Result(404, "record_not_found") : Results.Json(record, NotificationUseCases.Json);
        });
    }
}
