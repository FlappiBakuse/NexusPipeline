using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Extensions;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionAdapterObservation(string Format, int Version, StepEvidenceBinding Binding,
    BlobReference RawOutput, string SourceDigest, string TasksDigest, ImmutableArray<AdapterTaskObservation> Observations,
    ImmutableArray<string> ProposedRetry)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionTaskRetry? Retry { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<AdapterTaskObservation>? CurrentObservations { get; init; }
}

internal static class AdapterObservationCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = ObservationTypes(),
        Converters = { new JsonStringEnumConverter<AdapterObservationState>(JsonNamingPolicy.CamelCase, false),
            new JsonStringEnumConverter<AdapterCompletion>(JsonNamingPolicy.CamelCase, false) }
    };

    private static DefaultJsonTypeInfoResolver ObservationTypes()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Type != typeof(AdapterTaskObservation)) return;
            type.Properties.Single(property => property.Name == "completedRepetitions").ShouldSerialize = (_, value) => value is not null;
        });
        return resolver;
    }

    internal static BlobReference Write(ImmutableBlobStore blobs, StepEvidenceBinding binding, BlobReference raw,
        ExecutionPlanStep step, ImmutableArray<AdapterTaskObservation> observations, ImmutableArray<string> proposed,
        ExecutionTaskRetry? retry = null, ImmutableArray<AdapterTaskObservation>? current = null)
    {
        var document = new ExecutionAdapterObservation("nexus.execution.adapter-observation", 1, binding, raw,
            Digest(step.Adapter), Digest(step.Tasks), observations, proposed) { Retry = retry, CurrentObservations = current };
        Validate(blobs, binding, raw, step, document, 0);
        return blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(document, Options));
    }

    internal static ExecutionAdapterObservation Read(ImmutableBlobStore blobs, StepEvidenceBinding binding,
        BlobReference raw, ExecutionPlanStep step, BlobReference reference, int depth = 0)
    {
        if (depth is < 0 or > 98) throw Invalid();
        StepEvidenceCodec.Plan(reference);
        using var input = blobs.OpenVerified(reference); var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        using var parsed = StrictJson.Parse(bytes);
        var document = parsed.RootElement.Deserialize<ExecutionAdapterObservation>(Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(parsed.RootElement, JsonSerializer.SerializeToElement(document, Options))) throw Invalid();
        Validate(blobs, binding, raw, step, document, depth); return document;
    }

    private static void Validate(ImmutableBlobStore blobs, StepEvidenceBinding binding, BlobReference raw,
        ExecutionPlanStep step, ExecutionAdapterObservation document, int depth)
    {
        if (document.Format != "nexus.execution.adapter-observation" || document.Version != 1 || document.Binding != binding ||
            document.RawOutput != raw || step.StepId != binding.StepId || step.Adapter is null ||
            document.SourceDigest != Digest(step.Adapter) || document.TasksDigest != Digest(step.Tasks) || depth > 98) throw Invalid();
        var tasks = step.Tasks.Select(TaskAdapterProtocol.ReadTask).ToImmutableArray();
        AdapterTaskPolicy.RequireOriginalResults(tasks, document.Observations, document.ProposedRetry);
        var current = document.Observations;
        if (document.Retry is { } retry)
        {
            if (document.CurrentObservations is not { } fresh || retry.PreviousBinding is null || retry.PreviousBinding.JobName == binding.JobName ||
                retry.PreviousBinding with { JobName = binding.JobName, WriterId = binding.WriterId, SessionId = binding.SessionId } != binding)
                throw Invalid();
            var previous = Read(blobs, retry.PreviousBinding, retry.PreviousRaw, step, retry.PreviousObservation, depth + 1);
            var expected = AdapterRetryPolicy.Select(tasks, previous.Observations, previous.ProposedRetry);
            if (!TaskRetryEvidence.Same(expected, retry.Selection) ||
                !AdapterRetryPolicy.Merge(tasks, previous.Observations, retry.Selection, fresh).SequenceEqual(document.Observations)) throw Invalid();
            current = fresh;
        }
        else if (document.CurrentObservations is not null) throw Invalid();
        var output = RawOutputCodec.Read(blobs, binding, raw);
        if (!output.IsFinal || !output.IsComplete && (document.ProposedRetry.Length != 0 ||
            current.Any(item => item.State != AdapterObservationState.Unknown))) throw Invalid();
    }

    internal static IReadOnlyList<BlobReference> References(ImmutableBlobStore blobs, ExecutionPlanStep step,
        ExecutionAdapterObservation document)
    {
        var result = new List<BlobReference>();
        var depth = 0;
        while (document.Retry is { } retry)
        {
            if (++depth > 98) throw Invalid();
            result.Add(retry.PreviousObservation); result.Add(retry.PreviousRaw);
            var raw = RawOutputCodec.Read(blobs, retry.PreviousBinding, retry.PreviousRaw);
            result.AddRange(raw.Parts.Select(part => part.Content));
            result.AddRange(FileLogEvidence.References(blobs, raw));
            document = Read(blobs, retry.PreviousBinding, retry.PreviousRaw, step, retry.PreviousObservation);
        }
        return result.Distinct().ToArray();
    }

    private static string Digest<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static IOException Invalid() => new("execution_adapter_observation_unbound");
}
