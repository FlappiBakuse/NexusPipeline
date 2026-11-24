using NexusPipeline.Core.Control;
using NexusPipeline.Core.Records;
using NexusPipeline.Host.Control.Notifications;
using NexusPipeline.Host.Control.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Runtime.Versioning;

namespace NexusPipeline.Host.Control.Http;

[SupportedOSPlatform("windows")]
internal sealed class RecordControl(ControlAuthentication authority, RecordUseCases useCases, ContractReader contracts)
{
    private const string Attempt = "/control/v1/records/{runId}/attempts/{attemptId}";
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/records", (HttpContext context) =>
        {
            var request = authority.Require(context, ControlCapability.RecordsRead);
            var query = new ControlQueryValues(context.Request.Query, "limit", "automationId", "userId", "business",
                "afterEndedAt", "afterRunId", "endedFromInclusive", "endedUntilExclusive", "lifecycle", "scope");
            query.Validate();
            var afterTime = query.Utc("afterEndedAt"); var afterRun = query.OptionalId("afterRunId");
            if ((afterTime is null) != (afterRun is null)) throw new ControlRequestException(400, "query_invalid");
            return Json(useCases.Read(request, new(query.Integer("limit", 50), query.OptionalId("automationId"),
                query.OptionalId("userId"), query.Business("business"),
                afterTime is null ? null : new(afterTime.Value, afterRun!.Value),
                query.Utc("endedFromInclusive"), query.Utc("endedUntilExclusive"), query.Lifecycle("lifecycle"),
                context.Request.Query["scope"].ToString() switch { "" or "all" => RecordScope.All, "groups" => RecordScope.Groups,
                    _ => throw new ControlRequestException(400, "query_invalid") })));
        });
        app.MapGet("/control/v1/records/{runId}/attempts", (HttpContext context, string runId) =>
        {
            var request = authority.Require(context, ControlCapability.RecordsRead);
            var query = Query(context, "afterOrdinal", "limit");
            return Json(useCases.Attempts(request, new(ControlQueryValues.Id(runId),
                query.Integer("afterOrdinal", 0), query.Integer("limit", 50))));
        });
        app.MapGet(Attempt + "/logs", (HttpContext context, string runId, string attemptId) =>
        {
            var request = authority.Require(context, ControlCapability.LogsRead);
            var query = Query(context, "afterPosition", "limit");
            return Json(useCases.Logs(request, new(ControlQueryValues.Id(runId), ControlQueryValues.Id(attemptId),
                query.Integer("afterPosition", -1), query.Integer("limit", 50))));
        });
        app.MapGet(Attempt + "/images", (HttpContext context, string runId, string attemptId) =>
        {
            var request = authority.Require(context, ControlCapability.ImagesRead);
            var query = Query(context, "afterPosition", "limit");
            return Json(useCases.Images(request, new(ControlQueryValues.Id(runId), ControlQueryValues.Id(attemptId),
                query.Integer("afterPosition", -1), query.Integer("limit", 50))));
        });
        MapLogs(app);
        app.MapGet(Attempt + "/images/{stepId}/{captureId}/original",
            (HttpContext context, string runId, string attemptId, string stepId, string captureId) =>
        {
            var request = authority.Require(context, ControlCapability.ImagesRead);
            var query = Query(context, "resultSha256");
            var key = new RunImageKey(ControlQueryValues.Id(runId), ControlQueryValues.Id(attemptId), stepId,
                ControlQueryValues.Id(captureId));
            return Results.Stream(useCases.ImageOriginal(request, key, query.Digest("resultSha256")),
                "image/png", "capture.png", enableRangeProcessing: false);
        }).WithMetadata(ControlRequestLimits.ResponseStream);
        app.MapPost("/control/v1/records/{runId}/retire/{kind}", RetireAsync);
    }

    private void MapLogs(WebApplication app)
    {
        const string prefix = Attempt + "/logs/{stepId}/{role}";
        app.MapGet(prefix + "/description", (HttpContext context, string runId, string attemptId, string stepId, string role) =>
        {
            var request = authority.Require(context, ControlCapability.LogsRead); var query = Query(context, "invocationOrdinal");
            return Json(useCases.Describe(request, Key(runId, attemptId, stepId, role) with { InvocationOrdinal = Invocation(query) }));
        });
        app.MapGet(prefix + "/tail", (HttpContext context, string runId, string attemptId, string stepId, string role) =>
        {
            var request = authority.Require(context, ControlCapability.LogsRead);
            var query = Query(context, "maximumBytes", "invocationOrdinal");
            return Json(useCases.Tail(request, Key(runId, attemptId, stepId, role) with { InvocationOrdinal = Invocation(query) }, query.Integer("maximumBytes", 65536)));
        });
        app.MapGet(prefix + "/original", (HttpContext context, string runId, string attemptId, string stepId, string role) =>
        {
            var request = authority.Require(context, ControlCapability.LogsRead);
            var query = Query(context, "snapshotSha256", "invocationOrdinal");
            return Results.Stream(useCases.LogOriginal(request, Key(runId, attemptId, stepId, role) with { InvocationOrdinal = Invocation(query) }, query.Digest("snapshotSha256")),
                "application/octet-stream", "original.log", enableRangeProcessing: false);
        }).WithMetadata(ControlRequestLimits.ResponseStream);
        app.MapGet(prefix + "/parts/{index}", (HttpContext context, string runId, string attemptId, string stepId, string role, string index) =>
        {
            var request = authority.Require(context, ControlCapability.LogsRead);
            var query = Query(context, "snapshotSha256", "invocationOrdinal");
            if (!int.TryParse(index, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out var part) || part < 0 || part.ToString(System.Globalization.CultureInfo.InvariantCulture) != index)
                throw new ControlRequestException(400, "query_invalid");
            return Results.Stream(useCases.LogPart(request, Key(runId, attemptId, stepId, role) with { InvocationOrdinal = Invocation(query) },
                query.Digest("snapshotSha256"), part), "application/octet-stream", "part.log", enableRangeProcessing: false);
        }).WithMetadata(ControlRequestLimits.ResponseStream);
    }

    private async Task<IResult> RetireAsync(HttpContext context, string runId, string kind)
    {
        var request = authority.Require(context, ControlCapability.RecordsRetire); _ = Query(context);
        using var body = await ControlRequestBody.ReadAsync(context, 4096);
        body.RequireSchema(contracts, "control-record-retirement.schema.json");
        var root = body.Root;
        if (root.GetProperty("version").GetInt32() != 1)
            throw new ControlRequestException(400, "record_retirement_request_invalid");
        var operation = ControlRequestBody.Uuid(root.GetProperty("operationId"));
        var after = root.GetProperty("afterSha256").GetString() ?? throw new ControlRequestException(400, "query_invalid");
        var result = useCases.Retire(request, operation, ControlQueryValues.Id(runId), kind, after, root.GetProperty("limit").GetInt32());
        return Results.Json(result, NotificationUseCases.Json, statusCode: result is null ? 200 : 202);
    }

    private static ControlQueryValues Query(HttpContext context, params string[] fields)
    { var query = new ControlQueryValues(context.Request.Query, fields); query.Validate(); return query; }
    private static int? Invocation(ControlQueryValues query)
    {
        var ordinal = query.Integer("invocationOrdinal", 0);
        if (ordinal is < 0 or > 99) throw new ControlRequestException(400, "query_invalid");
        return ordinal == 0 ? null : ordinal;
    }
    private static RunLogKey Key(string runId, string attemptId, string stepId, string role) =>
        new(ControlQueryValues.Id(runId), ControlQueryValues.Id(attemptId), stepId, ControlQueryValues.Role(role));
    private static IResult Json(object value) => Results.Json(value, NotificationUseCases.Json);
}

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
            return Results.Json(records.Read(Query(context)), NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/records/summary/days", (HttpContext context) =>
        {
            authority.Require(context, ControlCapability.RecordsRead);
            return Results.Json(records.ReadDays(Query(context)), NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/records/summary/users", (HttpContext context) =>
        {
            authority.Require(context, ControlCapability.RecordsRead);
            return Results.Json(records.ReadUsers(Query(context)), NotificationUseCases.Json);
        });
        app.MapGet("/control/v1/records/{runId}", (HttpContext context, string runId) =>
        {
            authority.Require(context, ControlCapability.RecordsRead);
            var query = new ControlQueryValues(context.Request.Query, "userId"); query.Validate();
            var record = records.Read(ControlQueryValues.Id(runId), query.OptionalId("userId"));
            return record is null ? ControlErrors.Result(404, "record_not_found") : Results.Json(record, NotificationUseCases.Json);
        });
    }

    private static RecordSummaryQuery Query(HttpContext context)
    {
        var query = new ControlQueryValues(context.Request.Query, "endedFromInclusive", "endedUntilExclusive",
            "offsetMinutes", "userId", "business", "afterUserId", "lifecycle"); query.Validate();
        return new(query.Utc("endedFromInclusive") ?? throw new ControlRequestException(400, "query_invalid"),
            query.Utc("endedUntilExclusive") ?? throw new ControlRequestException(400, "query_invalid"),
            query.Integer("offsetMinutes", 0), query.OptionalId("userId"), query.Business("business"),
            query.OptionalId("afterUserId"), query.Lifecycle("lifecycle"));
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ExecutionReportControl(ControlAuthentication authority, ExecutionLogReader reader)
{
    internal void Map(WebApplication app)
    {
        app.MapGet("/control/v1/records/{runId}/attempts/{attemptId}/report", (HttpContext context, string runId, string attemptId) =>
        {
            _ = authority.Require(context, ControlCapability.RecordsRead);
            new ControlQueryValues(context.Request.Query).Validate();
            return Results.Json(reader.ReadReport(ControlQueryValues.Id(runId), ControlQueryValues.Id(attemptId)), NotificationUseCases.Json);
        });
    }
}
