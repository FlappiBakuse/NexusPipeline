using System.Runtime.Versioning;
using System.Collections.Immutable;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class StepEvidenceFactory(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease)
{
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);
    private readonly StepEvidenceReader _reader = new(instance, database, contracts);

    internal PreparedStepResult CreateInterruptedCollected(StepEvidenceBinding binding, BlobReference? providerControl = null, BlobReference? workspaceOutput = null)
    {
        lease.VerifyHeld(instance); RequireStopped(binding);
        var context = _reader.Context(binding);
        var latest = new JournalStore(instance, contracts).Read(binding.ParentJournalId);
        var prior = StepResultCodec.Read(latest.Root, context.Total).SingleOrDefault(item => item.Binding == binding);
        if (prior?.IsCollected == true)
            return new(StepEvidenceAuthority.Recovery(this, binding, _reader.Read(binding, prior.Latest.Artifact, true)));
        var closure = new JobClosureStore(instance, database, contracts).ReadForExecutionEvidence(binding.ParentJournalId,
            binding.WriterId, binding.JobName, binding.SessionId) ?? throw new IOException("writer_job_closure_missing");
        StepEvidenceDocument? observation = prior is null ? null : StepEvidenceCodec.Read(_blobs, binding, prior.Latest.Artifact);
        providerControl ??= observation?.ProviderControl ?? ProviderTerminalCodec.Read(latest.Root, context.Total)
            .SingleOrDefault(item => item.Binding == binding)?.Artifact;
        if (workspaceOutput is null && providerControl is not null)
            workspaceOutput = ProviderOutputCodec.Recover(instance, database, contracts, binding, providerControl);
        var engine = observation?.Engine is EngineOutcome.Succeeded or EngineOutcome.Failed ? observation.Engine :
            closure.WasExecuted ? EngineOutcome.Unknown : EngineOutcome.NotStarted;
        var business = observation?.Business ?? BusinessOutcome.Unverified;
        var reason = business != BusinessOutcome.Unverified || engine is EngineOutcome.Succeeded or EngineOutcome.Failed
            ? observation!.ReasonCode : "host_interrupted";
        BlobReference? judgement = observation?.Judgement;
        if (judgement is not null)
        {
            var script = JudgementEvidenceCodec.Read(_blobs, binding, judgement);
            var worker = WriterIntentCodec.ReadJobs(latest.Root, context.Total).Single(item => item.StepId == binding.StepId && item.Role == "judgement");
            judgement = JudgementEvidenceCodec.Write(_blobs, script with { Closure = _reader.ClosureReference(binding with
                { JobName = worker.JobName, WriterId = worker.WriterId, SessionId = worker.SessionId }) });
        }
        var artifact = StepEvidenceCodec.Write(_blobs, new(StepEvidenceCodec.Format, 1, binding, "collected", null,
            context.Root, observation?.ExitCode, engine, business, observation?.Generation ?? 0, reason,
            prior?.Latest.Artifact, _reader.ClosureReference(binding)) { Judgement = judgement, ProviderControl = providerControl ?? observation?.ProviderControl,
                WorkspaceOutput = workspaceOutput ?? observation?.WorkspaceOutput });
        return new(StepEvidenceAuthority.Recovery(this, binding, _reader.Read(binding, artifact, true)));
    }

    internal ExecutionStepEvidence CreateLive(StepEvidenceBinding binding, BlobReference rawOutput, bool collected,
        ProcessIdentity? root, int? exitCode, BlobReference? previousObservation = null, StepStopProof? stopProof = null,
        BlobReference? judgementEvidence = null, BlobReference? providerControl = null, BlobReference? taskAdapter = null, BlobReference? workspaceOutput = null)
    {
        lease.VerifyHeld(instance); var context = _reader.Context(binding);
        if (root != context.Root) throw new IOException("execution_step_native_root_unbound");
        var raw = RawOutputCodec.Read(_blobs, binding, rawOutput);
        _ = FileLogEvidence.Require(instance, contracts, _blobs, context.Total, binding, context.Job.FileLogBaseline, raw);
        var definition = _reader.Judgement(context.Total, binding.StepId);
        var judgement = definition.Language == NexusPipeline.Core.Automation.AutomationJudgementLanguage.Keywords ? new AttemptBusinessJudgement(binding.AttemptId, definition) : null;
        var continuous = judgement is not null && RawOutputReplay.Read(_blobs, raw, line => judgement.ObserveLine(judgement.InputIdentity, line));
        var script = judgementEvidence is null ? null : new JudgementEvidenceReader(instance, database, contracts).Read(context.Total, binding, judgementEvidence, collected);
        var decision = script is null ? judgement?.Decision : new JudgementEvidenceReader(instance, database, contracts).Replay(context.Total, script, definition);
        var business = script is not null ? decision?.Outcome ?? BusinessOutcome.Unverified : continuous ? judgement!.Business : BusinessOutcome.Unverified;
        if (root is null) business = BusinessOutcome.Unverified;
        var engine = root is null ? EngineOutcome.NotStarted : exitCode is null ? collected ? EngineOutcome.Unknown : EngineOutcome.Running :
            exitCode == 0 ? EngineOutcome.Succeeded : EngineOutcome.Failed;
        var reason = business != BusinessOutcome.Unverified ? decision!.ReasonCode : engine switch
        {
            EngineOutcome.Succeeded => "execution_engine_succeeded", EngineOutcome.Failed => "execution_engine_failed",
            EngineOutcome.NotStarted => "execution_not_started", _ => "execution_output_unverified"
        };
        if (previousObservation is { } previous)
        {
            var prior = _reader.Read(binding, previous, false);
            if (prior.Business != BusinessOutcome.Unverified) { business = prior.Business; reason = prior.ReasonCode; }
            if (prior.Engine is EngineOutcome.Succeeded or EngineOutcome.Failed) engine = prior.Engine;
        }
        var latest = new JournalStore(instance, contracts).Read(binding.ParentJournalId);
        var close = DispatchIntentCodec.Read(latest.Root, context.Total);
        var acceptedClose = _reader.AcceptedClose(binding);
        var suppresses = StepResultAdmission.SuppressesLocalStop(acceptedClose);
        if (judgement?.Mode == ExecutionJudgementMode.None && root is not null && (collected || exitCode is not null) &&
            !suppresses && (context.Total.Steps.Single(item => item.StepId == binding.StepId).Adapter is null ||
                context.Total.Steps.Single(item => item.StepId == binding.StepId).Purpose == "initialization"))
        {
            business = BusinessOutcome.NotApplicable; reason = "execution_judgement_not_applicable";
        }
        if (judgement?.Mode == ExecutionJudgementMode.None && root is not null && taskAdapter is not null &&
            collected && close is null && acceptedClose is null)
        {
            var step = context.Total.Steps.Single(item => item.StepId == binding.StepId);
            var observed = AdapterObservationCodec.Read(_blobs, binding, rawOutput, step, taskAdapter);
            var adapterDecision = AdapterBusinessPolicy.Decide(step.Tasks.Select(TaskAdapterProtocol.ReadTask).ToImmutableArray(), observed.Observations);
            business = adapterDecision.Outcome; reason = adapterDecision.ReasonCode;
        }
        var ordinary = context.Total.Steps.Single(item => item.StepId == binding.StepId);
        if (collected && raw.IsComplete && root is not null && exitCode == 0 && business == BusinessOutcome.Unverified &&
            ordinary.Adapter is null && ordinary.Provider is null && close is null && acceptedClose is null &&
            (judgement is { Mode: not ExecutionJudgementMode.None } && continuous || script is not null))
        {
            business = BusinessOutcome.Failed;
            reason = "execution_completion_missing";
        }
        if (close is not null || acceptedClose is not null)
        {
            var prior = previousObservation is null ? null : _reader.Read(binding, previousObservation, false);
            business = prior?.Business ?? (!suppresses ? business : BusinessOutcome.Unverified);
            if (prior is { Business: not BusinessOutcome.Unverified }) reason = prior.ReasonCode;
            else if (business == BusinessOutcome.NotApplicable) reason = "execution_judgement_not_applicable";
            else reason = engine switch
            {
                EngineOutcome.Succeeded => "execution_engine_succeeded", EngineOutcome.Failed => "execution_engine_failed",
                EngineOutcome.NotStarted => "execution_not_started", _ => "execution_output_unverified"
            };
        }
        var stop = stopProof is not null && !suppresses
            ? stopProof.Require(binding, root, raw, context.Job.ClosedElapsedMilliseconds) : null;
        if (stop is not null) reason = stop.ReasonCode;
        var artifact = StepEvidenceCodec.Write(_blobs, new(StepEvidenceCodec.Format, 1, binding,
            collected ? "collected" : "observed", rawOutput, root, exitCode, engine, business, script?.Exchanges.Length ?? 0, reason, previousObservation,
            collected ? _reader.ClosureReference(binding) : null) { Stop = stop, Judgement = judgementEvidence, ProviderControl = providerControl, TaskAdapter = taskAdapter,
                WorkspaceOutput = workspaceOutput });
        return _reader.Read(binding, artifact, collected);
    }

    internal ExecutionStepEvidence NormalizeUnpublishedAfterClose(StepEvidenceBinding binding, ExecutionStepEvidence evidence,
        StepResultState? prior)
    {
        var document = StepEvidenceCodec.Read(_blobs, binding, evidence.Artifact);
        var business = prior?.Latest.Business ?? BusinessOutcome.Unverified;
        if (document.Stop is null && document.Business == business) return evidence;
        var reason = prior is { Latest.Business: not BusinessOutcome.Unverified } ? prior.Latest.ReasonCode : evidence.Engine switch
            {
                EngineOutcome.Succeeded => "execution_engine_succeeded", EngineOutcome.Failed => "execution_engine_failed",
                EngineOutcome.NotStarted => "execution_not_started", _ => "execution_output_unverified"
            };
        var artifact = StepEvidenceCodec.Write(_blobs, document with { Stop = null, Business = business, ReasonCode = reason });
        return _reader.Read(binding, artifact, true);
    }

    internal T UseLive<T>(StepEvidenceBinding binding, ExecutionStepEvidence evidence, bool collected,
        ProcessIdentity? root, int? exitCode, Func<ExecutionStepEvidence, T> callback)
    {
        lease.VerifyHeld(instance);
        var document = StepEvidenceCodec.Read(_blobs, binding, evidence.Artifact);
        if (document.Root != root || document.ExitCode is not null && document.ExitCode != exitCode ||
            _reader.Read(binding, evidence.Artifact, collected) != evidence)
            throw new IOException("execution_step_native_evidence_changed");
        return Hold(binding, evidence, callback);
    }

    internal T UseRecovery<T>(StepEvidenceBinding binding, ExecutionStepEvidence evidence, Func<ExecutionStepEvidence, T> callback)
    {
        lease.VerifyHeld(instance); RequireStopped(binding);
        if (_reader.Read(binding, evidence.Artifact, true) != evidence) throw new IOException("execution_step_evidence_changed");
        return Hold(binding, evidence, callback);
    }

    private T Hold<T>(StepEvidenceBinding binding, ExecutionStepEvidence evidence, Func<ExecutionStepEvidence, T> callback)
    {
        var held = new List<IDisposable>();
        try
        {
            var pending = new Queue<BlobReference>(); pending.Enqueue(evidence.Artifact);
            var seen = new HashSet<BlobReference>();
            while (pending.TryDequeue(out var reference))
            {
                if (!seen.Add(reference)) continue;
                if (seen.Count > 16) throw new IOException("execution_evidence_chain_limit");
                held.Add(_blobs.OpenVerified(reference));
                var document = StepEvidenceCodec.Read(_blobs, binding, reference);
                if (document.RawOutput is { } raw) held.Add(_blobs.OpenVerified(raw));
                if (document.Closure is { } closure) held.Add(_blobs.OpenVerified(closure));
                if (document.PreviousObservation is { } previous) pending.Enqueue(previous);
            }
            foreach (var content in _reader.ReadReferences(binding, evidence.Artifact)) held.Add(_blobs.OpenVerified(content));
            return callback(evidence);
        }
        finally { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
    }

    private void RequireStopped(StepEvidenceBinding binding)
    {
        var context = _reader.Context(binding);
        AttemptTerminalEvidence.RequireStopped(database, binding.ParentJournalId);
        var closure = new JobClosureStore(instance, database, contracts).ReadForExecutionEvidence(binding.ParentJournalId,
            binding.WriterId, binding.JobName, binding.SessionId) ?? throw new IOException("writer_job_closure_missing");
        var document = new JournalStore(instance, contracts).Read(binding.ParentJournalId);
        var jobs = WriterIntentCodec.ReadJobs(document.Root, context.Total);
        if (closure.WasExecuted) ExecutionStepAdmission.RequireRequiredRoles(context.Total, jobs, binding.StepId, "process", document.Root);
        StepWritersEvidence.RequireStopped(instance, database, contracts, binding.ParentJournalId, jobs.Where(item => item.StepId == binding.StepId));
    }
}
