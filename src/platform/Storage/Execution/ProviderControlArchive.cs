using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ProviderControlArchive(string Format, int Version, StepEvidenceBinding Binding,
    ExecutionProviderSession Identity, BlobReference Bootstrap, ProcessIdentity Peer, long SizeBytes, string Sha256,
    ImmutableArray<BlobReference> Parts, string? FaultCode, BlobReference? FaultFrame);

internal sealed record ProviderControlReplay(ProviderControlArchive Archive,
    ExecutionProviderDecision? Terminal, long EventCount);

internal sealed class ProviderControlReader(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal const string Format = "nexus.execution.provider-control";
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);
    internal ExecutionProviderBootstrap Bootstrap(StepEvidenceBinding binding, BlobReference artifact) => Metadata(binding, artifact).Bootstrap;

    internal ProviderControlReplay Read(StepEvidenceBinding binding, BlobReference artifact)
    {
        var (archive, bootstrap) = Metadata(binding, artifact);
        byte[] bytes = archive.SizeBytes == 0 ? [] : JudgementFrameParts.Read(_blobs, archive.SizeBytes, archive.Sha256, archive.Parts,
            ProviderProtocolState.MaximumEncodedBytes);
        if (archive.FaultFrame is { } fault) using (_blobs.OpenVerified(fault)) { }
        var protocol = new ProviderProtocolState(archive.Identity);
        using var stream = new MemoryStream(bytes, writable: false);
        long count = 0;
        while (stream.Position < stream.Length)
        {
            var body = ExecutionProviderFrames.ReadAsync(stream, ExecutionProviderFrames.MaximumEventBytes, default).GetAwaiter().GetResult();
            var frame = contracts.Read<ExecutionProviderEvent>(body, "execution-provider-event.schema.json");
            if (frame.Identity != bootstrap.Identity || ++count > bootstrap.MaximumEvents) throw Invalid();
            if (!protocol.Observe(Signal(frame), checked(body.Length + 4))) throw Invalid();
        }
        return new(archive, protocol.Decision, count);
    }

    internal IReadOnlyList<BlobReference> ReadReferences(StepEvidenceBinding binding, BlobReference artifact)
    {
        var (archive, _) = Metadata(binding, artifact);
        return new[] { artifact, archive.Bootstrap }.Concat(archive.Parts).Concat(archive.FaultFrame is { } fault ? [fault] : Array.Empty<BlobReference>()).Distinct().ToArray();
    }

    internal IReadOnlyList<BlobReference> ReadValidatedReferences(StepEvidenceBinding binding, BlobReference artifact,
        (ExecutionPlan Total, StepWriterState Job, ProcessIdentity? Root) context, long attemptOrdinal)
    {
        var (archive, _) = Metadata(binding, artifact, context, attemptOrdinal);
        return new[] { artifact, archive.Bootstrap }.Concat(archive.Parts)
            .Concat(archive.FaultFrame is { } fault ? [fault] : Array.Empty<BlobReference>()).Distinct().ToArray();
    }

    private (ProviderControlArchive Archive, ExecutionProviderBootstrap Bootstrap) Metadata(
        StepEvidenceBinding binding, BlobReference artifact)
        => Metadata(binding, artifact, new StepEvidenceReader(instance, database, contracts).Context(binding),
            ProviderControlInputs.Ordinal(instance, database, contracts, binding));

    private (ProviderControlArchive Archive, ExecutionProviderBootstrap Bootstrap) Metadata(
        StepEvidenceBinding binding, BlobReference artifact,
        (ExecutionPlan Total, StepWriterState Job, ProcessIdentity? Root) context, long attemptOrdinal)
    {
        if (context.Job.StepId != binding.StepId || context.Job.JobName != binding.JobName || context.Job.WriterId != binding.WriterId ||
            context.Job.SessionId != binding.SessionId || context.Job.Role != "process" ||
            context.Total.InstanceId != binding.InstanceId.ToString("D") || context.Total.RunId != binding.RunId.ToString("D")) throw Invalid();
        var step = context.Total.Steps.Single(step => step.StepId == binding.StepId);
        if (step.Provider is null || step.EngineKind != "extensionWorker" || context.Root is null) throw Invalid();
        var archive = Decode<ProviderControlArchive>(artifact);
        if (archive.Identity is null || archive.Format != Format || archive.Version != 1 || archive.Binding != binding || archive.Peer != context.Root ||
            archive.FaultCode is not (null or "execution_provider_channel_incomplete" or "execution_provider_protocol_invalid" or
                "execution_provider_channel_deadline" or "execution_provider_admission_closed") ||
            archive.Identity != Identity(binding, step, archive.Identity.Nonce)) throw Invalid();
        if (archive.SizeBytes == 0)
        {
            if (!archive.Parts.IsEmpty || archive.Sha256 != Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>()))) throw Invalid();
        }
        else JudgementFrameParts.Require(archive.SizeBytes, archive.Sha256, archive.Parts,
            ProviderProtocolState.MaximumEncodedBytes);
        if (archive.FaultFrame is { } fault && (archive.FaultCode != "execution_provider_protocol_invalid" ||
            fault.SizeBytes is < 1 or > ExecutionProviderFrames.MaximumEventBytes ||
            fault.RelativePath != $"blobs/content/{fault.Sha256}.bin" || !FileTreePolicy.Digest(fault.Sha256))) throw Invalid();
        StepEvidenceCodec.Plan(archive.Bootstrap);
        var bootstrapBytes = ReadPlanBytes(archive.Bootstrap);
        var bootstrap = contracts.Read<ExecutionProviderBootstrap>(bootstrapBytes, "execution-provider-bootstrap.schema.json");
        RequireBootstrap(instance, context.Total, binding, archive.Identity, bootstrap, attemptOrdinal);
        return (archive, bootstrap);
    }

    internal static ExecutionProviderSession Identity(StepEvidenceBinding binding, ExecutionPlanStep step, string nonce) =>
        new(binding.InstanceId, binding.ParentOperationId, binding.ParentJournalId, binding.RunId, binding.AttemptId,
            binding.StepId, (step.Provider ?? throw Invalid()).ExtensionId, step.Provider.EntryId,
            Guid.Parse(step.Provider.ActivationId), binding.PlanDigest, nonce);

    internal static void RequireBootstrap(RuntimeInstance instance, ExecutionPlan total, StepEvidenceBinding binding,
        ExecutionProviderSession identity, ExecutionProviderBootstrap bootstrap, long attemptOrdinal)
    {
        var step = total.Steps.Single(step => step.StepId == binding.StepId);
        var provider = step.Provider ?? throw Invalid();
        var expected = Identity(binding, step, identity.Nonce);
        _ = new ProviderProtocolState(expected);
        var expectedIdentity = WireIdentity(expected);
        var targets = total.ConfigurationSlots.Where(slot => slot.StepId == step.StepId).Select(slot => slot.TargetPath).ToArray();
        var input = ProviderControlInputs.Build(step, identity, attemptOrdinal);
        ProviderWorkspace.RequireArchived(instance, identity, bootstrap.Workspace, input.Project is not null);
        if (attemptOrdinal is < 1 or > 99 || bootstrap.AttemptOrdinal != attemptOrdinal ||
            bootstrap.IdempotencyKey != RetryAuthorityPolicy.BindingKey(binding.RunId, binding.StepId) ||
            identity != expected || bootstrap.Identity != expectedIdentity || bootstrap.PipeName != "NexusExecutionProvider-" + identity.Nonce ||
            bootstrap.MaximumEvents != ProviderProtocolState.MaximumEvents || bootstrap.WorkingDirectory != step.WorkingDirectory ||
            bootstrap.Project != input.Project || bootstrap.Device != input.Device ||
            !bootstrap.ConfigurationTargets.SequenceEqual(targets) || !bootstrap.UpstreamArguments.SequenceEqual(provider.UpstreamArguments) ||
            !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(bootstrap.Upstream), JsonSerializer.SerializeToElement(provider.Upstream))) throw Invalid();
    }

    internal static ExecutionProviderEventIdentity WireIdentity(ExecutionProviderSession value) => new()
    {
        InstanceId = value.InstanceId.ToString("D"), ParentOperationId = value.ParentOperationId.ToString("D"),
        ParentJournalId = value.ParentJournalId.ToString("D"), RunId = value.RunId.ToString("D"),
        AttemptId = value.AttemptId.ToString("D"), StepId = value.StepId, ExtensionId = value.ExtensionId,
        EntryId = value.EntryId, ActivationId = value.ActivationId.ToString("D"), PlanDigest = value.PlanDigest, Nonce = value.Nonce
    };

    internal static ExecutionProviderSignal Signal(ExecutionProviderEvent value) => new(new(
        Guid.Parse(value.Identity.InstanceId), Guid.Parse(value.Identity.ParentOperationId), Guid.Parse(value.Identity.ParentJournalId),
        Guid.Parse(value.Identity.RunId), Guid.Parse(value.Identity.AttemptId), value.Identity.StepId, value.Identity.ExtensionId,
        value.Identity.EntryId, Guid.Parse(value.Identity.ActivationId), value.Identity.PlanDigest, value.Identity.Nonce),
        value.Sequence, Enum.Parse<ProviderEventKind>(value.Kind, true), value.Outcome is null ? null :
            Enum.Parse<EngineOutcome>(value.Outcome, true), value.ReasonCode);

    private T Decode<T>(BlobReference reference)
    {
        var bytes = ReadPlanBytes(reference);
        using var json = StrictJson.Parse(bytes);
        var value = json.RootElement.Deserialize<T>(StepResultCodec.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(json.RootElement, JsonSerializer.SerializeToElement(value, StepResultCodec.Options))) throw Invalid();
        return value;
    }

    private byte[] ReadPlanBytes(BlobReference reference)
    {
        StepEvidenceCodec.Plan(reference);
        using var input = _blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)reference.SizeBytes)]; input.ReadExactly(bytes); return bytes;
    }

    private static IOException Invalid() => new("execution_provider_control_unbound");
}
