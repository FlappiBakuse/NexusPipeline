using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Records;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class JudgementEvidenceReader(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);

    internal ExecutionJudgementEvidence Read(ExecutionPlan total, StepEvidenceBinding binding, BlobReference reference, bool collected)
    {
        var value = JudgementEvidenceCodec.Read(_blobs, binding, reference);
        var step = total.Steps.Single(item => item.StepId == binding.StepId);
        var frozen = step.JudgementWorker ?? throw Invalid();
        var journal = new JournalStore(instance, contracts).Read(binding.ParentJournalId);
        var job = WriterIntentCodec.ReadJobs(ExecutionInvocationCodec.ForJob(journal.Root, total, binding.JobName), total).SingleOrDefault(item => item.StepId == binding.StepId && item.Role == "judgement")
            ?? throw Invalid();
        if (job.JobName != value.Identity.WorkerJobName || job.JudgementOwnerId != value.Identity.OwnerId ||
            value.ScriptSha256 != frozen.ScriptSha256 || value.Exchanges.Length > frozen.MaximumInvocations) throw Invalid();
        var workerClosure = new JobClosureStore(instance, database, contracts).ReadForExecutionEvidence(binding.ParentJournalId, job.WriterId, job.JobName, job.SessionId);
        var children = new HashSet<(int, DateTime)>();
        long generation = 0, evidenceBytes = 0; var finalSeen = false;
        foreach (var item in value.Exchanges)
        {
            if (finalSeen) throw Invalid();
            var archive = JudgementInvocationArchive.Read(_blobs, item.Invocation);
            if (archive.Identity != value.Identity || archive.Generation != ++generation) throw Invalid();
            var allowance = JudgementEvidenceBudget.AllocateProbes(frozen.MaximumEvidenceBytes, evidenceBytes, archive.SizeBytes, archive.IsFinal);
            if (archive.ProbeEvidenceAllowance > allowance) throw Invalid();
            long probeBytes = 0;
            for (var ordinal = 0; ordinal < item.ProbeObservations.Length; ordinal++)
            {
                var probe = JudgementProbeArchive.Read(_blobs, item.ProbeObservations[ordinal]);
                if (probe.Identity != value.Identity || probe.Generation != generation || probe.InputSha256 != archive.InputSha256 ||
                    probe.Ordinal != ordinal + 1) throw Invalid();
                probeBytes = checked(probeBytes + probe.SizeBytes);
            }
            if (probeBytes > archive.ProbeEvidenceAllowance) throw Invalid();
            evidenceBytes = checked(evidenceBytes + archive.SizeBytes + (item.Response?.SizeBytes ?? 0) + probeBytes);
            if (evidenceBytes > frozen.MaximumEvidenceBytes) throw Invalid();
            finalSeen = archive.IsFinal;
            foreach (var child in item.InterpreterChildren)
            {
                if (value.Language != "python" || frozen.Python is not { } python || !children.Add((child.ProcessId, child.CreatedAtUtc)) ||
                    !StringComparer.OrdinalIgnoreCase.Equals(child.ImagePath, python.Path) || child.ImageSha256 != python.Sha256 ||
                    child.ProcessId <= 0 || child.CreatedAtUtc.Kind != DateTimeKind.Utc ||
                    workerClosure is not null && !workerClosure.Family.Contains(child)) throw Invalid();
            }
            if (value.Language == "python" && item.Response is { } response)
            {
                var decoded = JudgementWorkerProtocol.Decode<JudgementWorkerResponse>(JudgementEvidenceCodec.ReadBytes(_blobs, response));
                if (decoded.Result is not null && item.InterpreterChildren.Length != 1) throw Invalid();
            }
        }
        if (value.Closure is { } closure)
        {
            if (job.ClosedElapsedMilliseconds is null) throw Invalid();
            var stored = workerClosure ?? throw Invalid();
            var workerBinding = binding with { JobName = job.JobName, WriterId = job.WriterId, SessionId = job.SessionId };
            if (stored.RunElapsedMilliseconds != job.ClosedElapsedMilliseconds || closure != new StepEvidenceReader(instance, database, contracts).ClosureReference(workerBinding) ||
                stored.Root is { } root && (!StringComparer.OrdinalIgnoreCase.Equals(root.ImagePath, frozen.Broker.Path) || root.ImageSha256 != frozen.Broker.Sha256)) throw Invalid();
        }
        else if (collected) throw Invalid();
        return value;
    }

    internal BusinessJudgementDecision? Replay(ExecutionPlan total, ExecutionJudgementEvidence value, AutomationJudgement definition,
        VerifiedRecordRetirement? retirement = null, BlobReference? originalArtifact = null)
    {
        if (definition.Language.ToString().ToLowerInvariant() != value.Language || definition.Script is not { } source ||
            JudgementWorkerProtocol.Hash(JudgementWorkerProtocol.Utf8.GetBytes(source)) != value.ScriptSha256) throw Invalid();
        if (retirement is not null) retirement.RequireOriginalJudgement(originalArtifact ?? throw Invalid(), value);
        var invocations = new JudgementWorkerInvocation[value.Exchanges.Length];
        var inputs = new JudgementInputBuilder(instance, database, contracts);
        for (var index = 0; index < value.Exchanges.Length; index++)
        {
            var item = value.Exchanges[index];
            invocations[index] = JudgementInvocationArchive.Decode(_blobs, item.Invocation);
            if (retirement?.ReplacesRawReplay(value.Identity.Primary, item.RawOutput) != true)
                inputs.VerifyHistorical(total, invocations[index], item.RawOutput);
        }
        return JudgementEvidenceCodec.Replay(_blobs, value, definition, invocations).Decision;
    }

    private static IOException Invalid() => new("execution_judgement_evidence_unbound");
}
