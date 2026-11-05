using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ExecutionJudgementExchange(BlobReference RawOutput, BlobReference Invocation, BlobReference? Response, string? FaultCode)
{
    public ImmutableArray<ProcessIdentity> InterpreterChildren { get; init; } = [];
    public ImmutableArray<BlobReference> ProbeObservations { get; init; } = [];
}
internal sealed record ExecutionJudgementEvidence(string Format, int Version, JudgementWorkerIdentity Identity, string Language,
    string ScriptSha256, ImmutableArray<ExecutionJudgementExchange> Exchanges, BlobReference? Closure);

internal static class JudgementEvidenceCodec
{
    internal const string Format = "nexus.execution.judgement-evidence";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static BlobReference Write(ImmutableBlobStore blobs, ExecutionJudgementEvidence value)
    {
        Validate(value, value.Identity.Primary);
        return blobs.PersistPlan(BoundedJson.Serialize(value, Options));
    }

    internal static bool CanAppend(ExecutionJudgementEvidence value, bool final)
    {
        // One admitted Python identity can include a 32 KiB path escaped to six bytes per character.
        const int exchangeReserve = 256 * 1024;
        return BoundedJson.Serialize(value, Options).Length <= StrictJson.MaximumBytes - exchangeReserve * (final ? 1 : 2);
    }

    internal static ExecutionJudgementEvidence Read(ImmutableBlobStore blobs, StepEvidenceBinding primary, BlobReference reference)
    {
        var bytes = ReadBytes(blobs, reference);
        using var parsed = StrictJson.Parse(bytes);
        var value = parsed.RootElement.Deserialize<ExecutionJudgementEvidence>(Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(parsed.RootElement, JsonSerializer.SerializeToElement(value, Options))) throw Invalid();
        Validate(value, primary); return value;
    }

    internal static byte[] ReadBytes(ImmutableBlobStore blobs, BlobReference reference)
    {
        StepEvidenceCodec.Plan(reference);
        using var stream = blobs.OpenVerified(reference); var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }

    internal static IReadOnlyList<BlobReference> References(ImmutableBlobStore blobs, ExecutionJudgementEvidence value)
    {
        var references = new List<BlobReference>();
        foreach (var item in value.Exchanges)
        {
            references.Add(item.RawOutput); references.Add(item.Invocation);
            references.AddRange(item.ProbeObservations);
            foreach (var probe in item.ProbeObservations) references.AddRange(JudgementProbeArchive.Read(blobs, probe).Parts);
            references.AddRange(JudgementInvocationArchive.Read(blobs, item.Invocation).Parts);
            if (item.Response is { } response) references.Add(response);
            var raw = RawOutputCodec.Read(blobs, value.Identity.Primary, item.RawOutput);
            references.AddRange(FileLogEvidence.References(blobs, raw));
        }
        if (value.Closure is { } closure) references.Add(closure);
        return references.Distinct().ToArray();
    }

    internal static AttemptBusinessJudgement Replay(ImmutableBlobStore blobs, ExecutionJudgementEvidence value,
        NexusPipeline.Core.Automation.AutomationJudgement definition, IReadOnlyList<JudgementWorkerInvocation> verifiedInvocations)
    {
        if (verifiedInvocations.Count != value.Exchanges.Length) throw Invalid();
        var owner = AttemptBusinessJudgement.Replay(value.Identity.Primary.AttemptId, definition, value.Identity.OwnerId);
        var finalSeen = false;
        for (var index = 0; index < value.Exchanges.Length; index++)
        {
            var entry = value.Exchanges[index]; var invocation = verifiedInvocations[index];
            if (finalSeen) throw Invalid();
            if (invocation.IsFinal) { owner.RequestFinal(); finalSeen = true; }
            var accepted = invocation.IsFinal ? owner.TryBeginFinal() : owner.TryBeginPeriodic();
            if (accepted is null || invocation.Identity != value.Identity || invocation.Generation != accepted.Generation) throw Invalid();
            var probes = entry.ProbeObservations.Select(reference =>
                JudgementProbeArchive.Decode(blobs, reference)).ToArray();
            JudgementProbeProtocol.RequireExchanges(probes, invocation);
            if (entry.Response is { } response)
            {
                var result = JudgementWorkerProtocol.Decode<JudgementWorkerResponse>(ReadBytes(blobs, response));
                JudgementWorkerProtocol.Require(result, invocation);
                if (result.ProbeCount != probes.Length || result.ProbeTranscriptSha256 != JudgementProbeProtocol.Hash(probes)) throw Invalid();
                if (result.Result is { } decision) owner.Complete(accepted, decision);
                else owner.CompleteFault(accepted);
            }
            else owner.CompleteFault(accepted);
        }
        return owner;
    }

    private static void Validate(ExecutionJudgementEvidence value, StepEvidenceBinding primary)
    {
        if (value.Format != Format || value.Version != 1 || value.Identity is null || value.Identity.Primary != primary ||
            value.Identity.OwnerId == Guid.Empty || value.Identity.WorkerJobName == primary.JobName ||
            value.Language is not ("javascript" or "python") || !NexusPipeline.Core.Configuration.FileTreePolicy.Digest(value.ScriptSha256) ||
            value.Exchanges.IsDefault || value.Exchanges.Length > 4095) throw Invalid();
        foreach (var item in value.Exchanges)
        {
            if (item is null || item.ProbeObservations.IsDefault || item.ProbeObservations.Length > JudgementProbeProtocol.MaximumCalls ||
                item.ProbeObservations.Any(reference => reference is null)) throw Invalid();
            foreach (var probe in item.ProbeObservations) StepEvidenceCodec.Plan(probe);
            StepEvidenceCodec.Plan(item.RawOutput); StepEvidenceCodec.Plan(item.Invocation);
            if ((item.Response is null) == (item.FaultCode is null)) throw Invalid();
            if (item.InterpreterChildren.IsDefault || item.InterpreterChildren.Length > 1 || item.Response is null && !item.InterpreterChildren.IsEmpty ||
                value.Language == "javascript" && !item.InterpreterChildren.IsEmpty) throw Invalid();
            if (item.Response is { } response) StepEvidenceCodec.Plan(response);
            if (item.FaultCode is { } fault && (fault != "judgement_worker_transport_faulted" && fault != "judgement_worker_stopped")) throw Invalid();
        }
        if (value.Closure is { } closure) StepEvidenceCodec.Plan(closure);
    }

    private static IOException Invalid() => new("execution_judgement_evidence_unbound");
}
