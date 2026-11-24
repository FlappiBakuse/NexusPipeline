using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record HookEvidenceDocument(string Format, int Version, StepEvidenceBinding Binding,
    string Role, BlobReference? RawOutput, BlobReference Closure, ProcessIdentity? Root, int? ExitCode,
    EngineOutcome Engine, string ReasonCode);

internal sealed class HookEvidenceReader(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal const string Format = "nexus.execution.hook-evidence";
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);

    internal HookEvidenceDocument Read(StepEvidenceBinding binding, string role, BlobReference artifact, bool verifyContents = true) =>
        ReadCore(binding, role, artifact, verifyContents, null).Document;

    internal HookEvidenceDocument ReadFromValidatedContext(StepEvidenceBinding binding, string role, BlobReference artifact,
        (ExecutionPlan Total, StepWriterState Job, ProcessIdentity? Root) context, bool verifyContents = true) =>
        ReadCore(binding, role, artifact, verifyContents, context).Document;

    internal (BlobReference Reference, RawOutputManifest Raw)? ReadValidatedOutput(StepEvidenceBinding binding, string role, BlobReference artifact,
        (ExecutionPlan Total, StepWriterState Job, ProcessIdentity? Root) context) =>
        ReadCore(binding, role, artifact, verifyContents: false, context).Output;

    private (HookEvidenceDocument Document, (BlobReference Reference, RawOutputManifest Raw)? Output) ReadCore(
        StepEvidenceBinding binding, string role, BlobReference artifact, bool verifyContents,
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
            return (value, (reference, raw));
        }
        else if (value.ExitCode is not null) throw Invalid();
        return (value, null);
    }

    internal IReadOnlyList<BlobReference> ReadReferences(StepEvidenceBinding binding, string role, BlobReference artifact)
        => References(ReadCore(binding, role, artifact, verifyContents: false, null), artifact);

    internal IReadOnlyList<BlobReference> ReadValidatedReferences(StepEvidenceBinding binding, string role, BlobReference artifact,
        (ExecutionPlan Total, StepWriterState Job, ProcessIdentity? Root) context) =>
        References(ReadCore(binding, role, artifact, verifyContents: false, context), artifact);

    private static IReadOnlyList<BlobReference> References(
        (HookEvidenceDocument Document, (BlobReference Reference, RawOutputManifest Raw)? Output) evidence, BlobReference artifact)
    {
        var refs = new List<BlobReference> { artifact, evidence.Document.Closure };
        if (evidence.Output is { } output)
        {
            refs.Add(output.Reference);
            refs.AddRange(output.Raw.Parts.Select(part => part.Content));
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

internal sealed record HookResultIntent(string Format, int Version, string StepId, string Role,
    StepEvidenceBinding Binding, BlobReference Artifact, EngineOutcome Engine, string ReasonCode);

internal static class HookResultCodec
{
    internal const string Format = "nexus.execution.hook-result";

    internal static bool IsParticipant(JsonElement participant)
    {
        if (!ExecutionParticipantText.MatchesFormat(participant, Format)) return false;
        _ = Decode(participant); return true;
    }

    internal static ImmutableArray<HookResultIntent> Read(AttemptJournalView journal, NexusPipeline.Platform.Serialization.Protocol.ExecutionPlan total)
    {
        journal = ExecutionInvocationCodec.Current(journal);
        var jobs = WriterIntentCodec.ReadJobs(journal, total);
        var result = ImmutableArray.CreateBuilder<HookResultIntent>();
        foreach (var participant in journal.Participants)
        {
            if (!ExecutionParticipantText.MatchesFormat(participant, Format)) continue;
            var item = Decode(participant);
            var step = total.Steps.SingleOrDefault(step => step.StepId == item.StepId) ?? throw Invalid();
            var job = jobs.SingleOrDefault(job => job.StepId == item.StepId && job.Role == item.Role) ?? throw Invalid();
            if (item.Binding != StepResultCodec.Binding(journal, job) || job.ClosedElapsedMilliseconds is null ||
                result.Any(prior => prior.StepId == item.StepId && prior.Role == item.Role) ||
                participant.GetProperty("resourceKey").GetString() != "automation:" + step.ScriptId) throw Invalid();
            _ = ExecutionBindingRoles.Hook(step, item.Role);
            result.Add(item);
        }
        return result.ToImmutable();
    }

    internal static ImmutableArray<HookResultIntent> ReadHistory(AttemptJournalView journal, NexusPipeline.Platform.Serialization.Protocol.ExecutionPlan total) =>
        ExecutionInvocationCodec.Views(journal).SelectMany(view => Read(view, total)).DistinctBy(item => item.Binding.JobName).ToImmutableArray();

    internal static JsonObject Participant(HookResultIntent item, string scriptId) => new()
    {
        ["participantId"] = Guid.NewGuid().ToString("D"), ["kind"] = "database", ["resourceKey"] = "automation:" + scriptId,
        ["identity"] = JsonSerializer.SerializeToNode(item, StepResultCodec.Options)
    };

    private static HookResultIntent Decode(JsonElement participant)
    {
        var identity = participant.GetProperty("identity");
        var item = identity.Deserialize<HookResultIntent>(StepResultCodec.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(identity, JsonSerializer.SerializeToElement(item, StepResultCodec.Options)) ||
            item.Format != Format || item.Version != 1 || string.IsNullOrWhiteSpace(item.StepId) || item.StepId.Length > 120 ||
            !ExecutionBindingRoles.IsHook(item.Role) || item.Binding is null || item.Binding.StepId != item.StepId ||
            !Enum.IsDefined(item.Engine) || item.Engine == EngineOutcome.Running ||
            item.ReasonCode is not ("execution_hook_succeeded" or "execution_hook_failed" or "execution_hook_not_started" or "execution_hook_output_unverified" or "host_interrupted") ||
            participant.GetProperty("kind").GetString() != "database") throw Invalid();
        StepEvidenceCodec.Plan(item.Artifact);
        return item;
    }

    private static JsonException Invalid() => new("execution_hook_result_unrecognized");
}

[SupportedOSPlatform("windows")]
internal sealed class PreparedHookResult
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly ConfigurationWriterProcess? _writer;
    private readonly ConfigurationRunController? _controller;
    private readonly StoppedConfigurationEditor? _stopped;
    internal StepEvidenceBinding Binding { get; }
    internal string Role { get; }
    private readonly BlobReference _artifact;

    private PreparedHookResult(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        StepEvidenceBinding binding, string role, BlobReference artifact, ConfigurationWriterProcess? writer, StoppedConfigurationEditor? stopped, ConfigurationRunController? controller = null)
    {
        _instance = instance; _database = database; _contracts = contracts; _lease = lease;
        Binding = binding; Role = role; _artifact = artifact; _writer = writer; _stopped = stopped; _controller = controller;
    }

    internal static PreparedHookResult Live(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        StepEvidenceBinding binding, string role, BlobReference raw, ConfigurationWriterProcess writer, StoppedConfigurationEditor stopped)
    {
        lease.VerifyHeld(instance);
        return writer.UseStepEvidence<PreparedHookResult>(binding, stopped, (root, code) =>
        {
            var engine = root is null ? EngineOutcome.NotStarted : code is null ? EngineOutcome.Unknown : code == 0 ? EngineOutcome.Succeeded : EngineOutcome.Failed;
            var closure = new StepEvidenceReader(instance, database, contracts).ClosureReference(binding);
            var document = new HookEvidenceDocument(HookEvidenceReader.Format, 1, binding, role, raw, closure,
                root, code, engine, HookEvidenceReader.Reason(engine));
            var artifact = new ImmutableBlobStore(instance, contracts).PersistPlan(JsonSerializer.SerializeToUtf8Bytes(document, StepResultCodec.Options));
            return new(instance, database, contracts, lease, binding, role, artifact, writer, stopped);
        });
    }

    internal static PreparedHookResult Live(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        StepEvidenceBinding binding, string role, BlobReference raw, ConfigurationRunController controller, StoppedConfigurationEditor stopped)
    {
        lease.VerifyHeld(instance);
        return controller.UseStepEvidence<PreparedHookResult>(binding, stopped, (root, code) =>
        {
            var engine = root is null ? EngineOutcome.NotStarted : code is null ? EngineOutcome.Unknown : code == 0 ? EngineOutcome.Succeeded : EngineOutcome.Failed;
            var closure = new StepEvidenceReader(instance, database, contracts).ClosureReference(binding);
            var document = new HookEvidenceDocument(HookEvidenceReader.Format, 1, binding, role, raw, closure,
                root, code, engine, HookEvidenceReader.Reason(engine));
            var artifact = new ImmutableBlobStore(instance, contracts).PersistPlan(JsonSerializer.SerializeToUtf8Bytes(document, StepResultCodec.Options));
            return new(instance, database, contracts, lease, binding, role, artifact, null, stopped, controller);
        });
    }

    internal static PreparedHookResult Interrupted(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        StepEvidenceBinding binding, string role)
    {
        lease.VerifyHeld(instance);
        var reader = new StepEvidenceReader(instance, database, contracts);
        var context = reader.Context(binding, role);
        StepWritersEvidence.RequireStopped(instance, database, contracts, binding.ParentJournalId, [context.Job]);
        var closure = reader.ClosureReference(binding);
        var engine = context.Root is null ? EngineOutcome.NotStarted : EngineOutcome.Unknown;
        var document = new HookEvidenceDocument(HookEvidenceReader.Format, 1, binding, role, null, closure,
            context.Root, null, engine, HookEvidenceReader.Reason(engine));
        var artifact = new ImmutableBlobStore(instance, contracts).PersistPlan(JsonSerializer.SerializeToUtf8Bytes(document, StepResultCodec.Options));
        return new(instance, database, contracts, lease, binding, role, artifact, null, null);
    }

    internal T Use<T>(StepEvidenceBinding expected, string role, Func<BlobReference, HookEvidenceDocument, T> callback)
    {
        _lease.VerifyHeld(_instance);
        if (expected != Binding || role != Role) throw new IOException("execution_hook_native_owner_unbound");
        return _writer is null && _controller is null ? UseRecovery() : UseNative((root, code) =>
        {
            var value = Read();
            if (value.Root != root || value.ExitCode != code) throw new IOException("execution_hook_native_owner_changed");
            return Hold(value);
        });

        T UseNative(Func<ProcessIdentity?, int?, T> read) => _writer is not null
            ? _writer.UseStepEvidence(Binding, _stopped, read) : _controller!.UseStepEvidence(Binding, _stopped, read);
        T UseRecovery()
        {
            var context = new StepEvidenceReader(_instance, _database, _contracts).Context(Binding, Role);
            StepWritersEvidence.RequireStopped(_instance, _database, _contracts, Binding.ParentJournalId, [context.Job]);
            return Hold(Read());
        }
        HookEvidenceDocument Read() => new HookEvidenceReader(_instance, _database, _contracts).Read(Binding, Role, _artifact);
        T Hold(HookEvidenceDocument value)
        {
            var held = new List<IDisposable>();
            try
            {
                var blobs = new ImmutableBlobStore(_instance, _contracts);
                foreach (var reference in new HookEvidenceReader(_instance, _database, _contracts).ReadReferences(Binding, Role, _artifact))
                    held.Add(blobs.OpenVerified(reference));
                return callback(_artifact, value);
            }
            finally { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
        }
    }
}
