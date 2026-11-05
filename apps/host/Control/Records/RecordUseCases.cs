using System.Runtime.Versioning;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Records;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Storage.Records;

namespace NexusPipeline.Host.Control.Records;

[SupportedOSPlatform("windows")]
internal sealed class RecordUseCases(IRunRecordStore records, IAttemptHistoryReader attempts,
    IRunLogCatalog logCatalog, IRunLogReader logs, IRunImageCatalog images, ContentRetirementService retirement)
{
    internal RecordPage Read(AuthenticatedControlRequest request, RecordQuery query)
    { request.Require(ControlCapability.RecordsRead); return records.ReadPage(query); }
    internal AttemptHistoryPage Attempts(AuthenticatedControlRequest request, AttemptHistoryQuery query)
    { request.Require(ControlCapability.RecordsRead); return attempts.ReadPage(query); }
    internal LogCatalogPage Logs(AuthenticatedControlRequest request, LogCatalogQuery query)
    { request.Require(ControlCapability.LogsRead); return logCatalog.ReadPage(query); }
    internal RunLogDescription Describe(AuthenticatedControlRequest request, RunLogKey key)
    { request.Require(ControlCapability.LogsRead); return logs.Describe(key); }
    internal RunLogTail Tail(AuthenticatedControlRequest request, RunLogKey key, int maximumBytes)
    { request.Require(ControlCapability.LogsRead); return logs.ReadTail(key, maximumBytes); }
    internal Stream LogOriginal(AuthenticatedControlRequest request, RunLogKey key, string snapshot)
    { request.Require(ControlCapability.LogsRead); return logs.OpenOriginal(key, snapshot); }
    internal Stream LogPart(AuthenticatedControlRequest request, RunLogKey key, string snapshot, int index)
    { request.Require(ControlCapability.LogsRead); return logs.OpenPart(key, snapshot, index); }
    internal ImageCatalogPage Images(AuthenticatedControlRequest request, ImageCatalogQuery query)
    { request.Require(ControlCapability.ImagesRead); return images.ReadPage(query); }
    internal Stream ImageOriginal(AuthenticatedControlRequest request, RunImageKey key, string result)
    { request.Require(ControlCapability.ImagesRead); return images.OpenOriginal(key, result); }
    internal ContentRetirementPage? Retire(AuthenticatedControlRequest request, Guid operation, Guid run,
        string kind, string after, int limit)
    {
        request.Require(ControlCapability.RecordsRetire);
        return kind switch
        {
            "logs" => retirement.RetireLogsPage(operation, request.SubjectId, run, after, limit),
            "images" => retirement.RetireImagesPage(operation, request.SubjectId, run, after, limit),
            _ => throw new ControlRequestException(400, "record_content_kind_invalid")
        };
    }
}
