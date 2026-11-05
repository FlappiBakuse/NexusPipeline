using System.Text.Json.Serialization;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Media;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionCaptureBinding(StepEvidenceBinding Primary, Guid CaptureId,
    string TotalPlanSha256, string HelperJobName);

internal sealed record ExecutionCaptureIntent(string Format, int Version, ExecutionCaptureBinding Binding,
    string Stage, long BeforeRevision, BlobReference Artifact);

internal sealed record CaptureSourceDocument(string Format, int Version, ExecutionCaptureBinding Binding,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PcWindowFacts? Window,
    ExecutionPlanInterpreterImage HelperImage, JournalWriter Creator)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AndroidCaptureFacts? Android { get; init; }
}

internal sealed record AndroidCaptureFacts(AndroidControlBinding Binding, BlobReference Startup,
    string Endpoint, string Package)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MuMuCaptureFacts? MuMu { get; init; }
}

internal sealed record MuMuCaptureFacts(long Index, long EndpointPort, BlobReference InfoEvidence,
    string ManagerImageSha256);

internal sealed record CaptureRootDocument(string Format, int Version, ExecutionCaptureBinding Binding,
    BlobReference Source, ProcessIdentity NativeRoot);

internal sealed record CaptureResultDocument(string Format, int Version, ExecutionCaptureBinding Binding,
    BlobReference Source, BlobReference? Root, string Outcome, string ReasonCode,
    OwnedTargetInventory? HelperStopped, BlobReference ParentClosure, BlobReference? Png)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CapturePngInfo? Image { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CaptureFrameRecord>? Frames { get; init; }
}

internal sealed record ExecutionCaptureState(ExecutionCaptureBinding Binding, BlobReference Source,
    BlobReference? Root, BlobReference? Result);
