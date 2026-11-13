using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionStepEvidence(BlobReference Artifact, EngineOutcome Engine, BusinessOutcome Business, long Generation, string ReasonCode);
internal sealed record StepEvidenceDocument(string Format, int Version, StepEvidenceBinding Binding, string Stage,
    BlobReference? RawOutput, ProcessIdentity? Root, int? ExitCode, EngineOutcome Engine, BusinessOutcome Business,
    long Generation, string ReasonCode, BlobReference? PreviousObservation, BlobReference? Closure)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StepStopObservation? Stop { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BlobReference? Judgement { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BlobReference? ProviderControl { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BlobReference? TaskAdapter { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BlobReference? WorkspaceOutput { get; init; }
}

internal static class StepEvidenceCodec
{
    internal const string Format = "nexus.execution.step-evidence";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<EngineOutcome>(JsonNamingPolicy.CamelCase, false),
            new JsonStringEnumConverter<BusinessOutcome>(JsonNamingPolicy.CamelCase, false) }
    };

    internal static BlobReference Write(ImmutableBlobStore blobs, StepEvidenceDocument document)
    { Validate(document, document.Binding); return blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(document, Options)); }

    internal static StepEvidenceDocument Read(ImmutableBlobStore blobs, StepEvidenceBinding expected, BlobReference reference)
    {
        Plan(reference);
        using var stream = blobs.OpenVerified(reference);
        var value = BlobReadStream.ReadEvidence(stream, () =>
        {
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            using var parsed = StrictJson.Parse(bytes);
            var document = parsed.RootElement.Deserialize<StepEvidenceDocument>(Options) ?? throw Invalid();
            if (!JsonElement.DeepEquals(parsed.RootElement, JsonSerializer.SerializeToElement(document, Options))) throw Invalid();
            Validate(document, expected); return document;
        });
        if (value.Binding != expected) throw Invalid();
        return value;
    }

    internal static void Plan(BlobReference reference)
    {
        if (reference is null || reference.SizeBytes is < 1 or > StrictJson.MaximumBytes || reference.Sha256 is not { Length: 64 } ||
            reference.Sha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            reference.RelativePath != $"blobs/plans/{reference.Sha256}.json") throw Invalid();
    }

    private static void Validate(StepEvidenceDocument value, StepEvidenceBinding expected)
    {
        if (value.Format != Format || value.Version != 1 || value.Binding != expected || value.Stage is not ("observed" or "collected") ||
            !Enum.IsDefined(value.Engine) || !Enum.IsDefined(value.Business) || value.Generation is < 0 or > 9007199254740991 ||
            value.ReasonCode is null || value.ReasonCode.Length is < 1 or > 80 || value.ReasonCode.Any(character =>
                character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')) ||
            (value.Stage == "collected") != (value.Closure is not null) || value.Stage == "collected" && value.Engine == EngineOutcome.Running ||
            value.ExitCode is not null && value.Root is null || value.Engine == EngineOutcome.NotStarted && value.Root is not null ||
            value.Engine is EngineOutcome.Running or EngineOutcome.Unknown && value.Root is null ||
            value.Engine == EngineOutcome.Succeeded && value.ExitCode != 0 || value.Engine == EngineOutcome.Failed && value.ExitCode is null or 0 ||
            value.Engine is EngineOutcome.NotStarted or EngineOutcome.Running or EngineOutcome.Unknown && value.ExitCode is not null ||
            value.Engine == EngineOutcome.Cancelled || value.Business == BusinessOutcome.Cancelled ||
            value.Business == BusinessOutcome.NotApplicable && (value.Root is null || value.Stage != "collected" && value.ExitCode is null ||
                value.ReasonCode != "execution_judgement_not_applicable" && value.ReasonCode != value.Stop?.ReasonCode) ||
            value.Generation != 0 && value.Judgement is null) throw Invalid();
        if (value.TaskAdapter is not null && (value.Stage != "collected" || value.RawOutput is null)) throw Invalid();
        if (value.WorkspaceOutput is not null && (value.Stage != "collected" || value.ProviderControl is null || value.Root is null)) throw Invalid();
        if (value.Stop is { } stop && (value.Stage != "collected" || value.Root is null ||
            stop.ElapsedMilliseconds < 0 || stop.LastOutputElapsedMilliseconds < 0 || stop.LastOutputElapsedMilliseconds > stop.ElapsedMilliseconds ||
            stop.ObservedBytes < 0 || stop.BusinessAcceptedElapsedMilliseconds < 0 || stop.BusinessAcceptedElapsedMilliseconds > stop.ElapsedMilliseconds ||
            stop.ReasonCode is not ("execution_business_failed" or "execution_success_grace_expired" or "execution_step_stall_timeout" or "execution_step_total_timeout")))
            throw Invalid();
        foreach (var reference in new[] { value.RawOutput, value.PreviousObservation, value.Closure, value.Judgement, value.ProviderControl, value.TaskAdapter,
            value.WorkspaceOutput }.OfType<BlobReference>()) Plan(reference);
    }

    private static JsonException Invalid() => new("execution_step_evidence_unrecognized");
}
