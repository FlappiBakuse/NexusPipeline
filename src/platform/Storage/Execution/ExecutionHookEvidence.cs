using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Records;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record HookEvidenceDocument(string Format, int Version, StepEvidenceBinding Binding,
    string Role, BlobReference? RawOutput, BlobReference Closure, ProcessIdentity? Root, int? ExitCode,
    EngineOutcome Engine, string ReasonCode);

internal sealed class HookEvidenceReader(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal const string Format = "nexus.execution.hook-evidence";
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);

    internal HookEvidenceDocument Read(StepEvidenceBinding binding, string role, BlobReference artifact, bool verifyContents = true) =>
        ReadCore(binding, role, artifact, verifyContents, null);

    internal HookEvidenceDocument ReadFromValidatedContext(StepEvidenceBinding binding, string role, BlobReference artifact,
        (ExecutionPlan Total, StepWriterState Job, ProcessIdentity? Root) context, bool verifyContents = true) =>
        ReadCore(binding, role, artifact, verifyContents, context);

    private HookEvidenceDocument ReadCore(StepEvidenceBinding binding, string role, BlobReference artifact, bool verifyContents,
        (ExecutionPlan Total, StepWriterState Job, ProcessIdentity? Root)? validatedContext)
    {
        StepEvidenceCodec.Plan(artifact);
        using var input = _blobs.OpenVerified(artifact);
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        using var parsed = StrictJson.Parse(bytes);
        var value = parsed.RootElement.Deserialize<HookEvidenceDocument>(StepResultCodec.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(parsed.RootElement, JsonSerializer.SerializeToElement(value, StepResultCodec.Options)) ||
            value.Format != Format || value.Version != 1 || value.Binding != binding || value.Role != role || !ExecutionBindingRoles.IsHook(role) ||
            !Enum.IsDefined(value.Engine) || value.Engine is EngineOutcome.Running or EngineOutcome.Cancelled ||
            value.ReasonCode != Reason(value.Engine)) throw Invalid();
        var reader = new StepEvidenceReader(instance, database, contracts);
        var context = validatedContext ?? reader.Context(binding, role);
        var closure = new JobClosureStore(instance, database, contracts).ReadForExecutionEvidence(binding.ParentJournalId,
            binding.WriterId, binding.JobName, binding.SessionId) ?? throw Invalid();
        if (value.Closure != reader.ClosureReference(binding) || closure.InstanceId != binding.InstanceId ||
            closure.OperationId != binding.ParentOperationId || closure.JournalIds.Length != 1 || closure.JournalIds[0] != binding.ParentJournalId ||
            closure.RunElapsedMilliseconds != context.Job.ClosedElapsedMilliseconds || context.Job.ClosedElapsedMilliseconds is null ||
            value.Root != context.Root || closure.Root != context.Root || (value.Engine == EngineOutcome.NotStarted) != !closure.WasExecuted ||
            value.ExitCode.HasValue && value.RawOutput is null ||
            value.Engine != (value.Root is null ? EngineOutcome.NotStarted : value.ExitCode is null ? EngineOutcome.Unknown :
                value.ExitCode == 0 ? EngineOutcome.Succeeded : EngineOutcome.Failed)) throw Invalid();
        if (value.RawOutput is { } reference)
        {
            var raw = RawOutputCodec.Read(_blobs, binding, reference);
            if (!raw.IsFinal || raw.FileLogBaseline is not null || !raw.FileSources.IsEmpty) throw Invalid();
            if (verifyContents)
            {
                var retirement = VerifiedRecordRetirement.ForArtifact(instance, database, contracts, binding, role, artifact);
                foreach (var part in raw.Parts)
                    if (retirement?.Contains(part.Content) != true) _blobs.Verify(part.Content);
            }
        }
        else if (value.ExitCode is not null) throw Invalid();
        return value;
    }

    internal IReadOnlyList<BlobReference> ReadReferences(StepEvidenceBinding binding, string role, BlobReference artifact)
        => References(Read(binding, role, artifact, verifyContents: false), binding, artifact);

    internal IReadOnlyList<BlobReference> ReadValidatedReferences(StepEvidenceBinding binding, string role, BlobReference artifact,
        (ExecutionPlan Total, StepWriterState Job, ProcessIdentity? Root) context) =>
        References(ReadFromValidatedContext(binding, role, artifact, context, verifyContents: false), binding, artifact);

    private IReadOnlyList<BlobReference> References(HookEvidenceDocument value, StepEvidenceBinding binding, BlobReference artifact)
    {
        var refs = new List<BlobReference> { artifact, value.Closure };
        if (value.RawOutput is { } reference)
        {
            refs.Add(reference);
            refs.AddRange(RawOutputCodec.Read(_blobs, binding, reference).Parts.Select(part => part.Content));
        }
        return refs.Distinct().ToArray();
    }

    internal static string Reason(EngineOutcome engine) => engine switch
    {
        EngineOutcome.Succeeded => "execution_hook_succeeded",
        EngineOutcome.Failed => "execution_hook_failed",
        EngineOutcome.NotStarted => "execution_hook_not_started",
        EngineOutcome.Unknown => "execution_hook_output_unverified",
        _ => throw Invalid()
    };
    private static IOException Invalid() => new("execution_hook_evidence_unbound");
}
