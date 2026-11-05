using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Records;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class StepEvidenceReader(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);
    internal ExecutionStepEvidence Read(StepEvidenceBinding binding, BlobReference artifact, bool collected)
    {
        var context = Context(binding);
        return ReadFromValidatedContext(binding, artifact, collected, context);
    }

    internal ExecutionStepEvidence ReadFromValidatedContext(StepEvidenceBinding binding, BlobReference artifact, bool collected,
        (ExecutionPlan Total, StepWriterState Job, ProcessIdentity? Root) context)
    {
        var retirement = VerifiedRecordRetirement.ForArtifact(instance, database, contracts, binding, "process", artifact);
        var document = ReadDocument(binding, artifact, collected, context, verifyContents: true, 0, retirement: retirement);
        return new(artifact, document.Engine, document.Business, document.Generation, document.ReasonCode);
    }

    internal IReadOnlyList<BlobReference> ReadReferences(StepEvidenceBinding binding, BlobReference artifact) =>
        ReadValidatedReferences(binding, artifact, Context(binding));

    internal IReadOnlyList<BlobReference> ReadValidatedReferences(StepEvidenceBinding binding, BlobReference artifact,
        (ExecutionPlan Total, StepWriterState Job, ProcessIdentity? Root) context)
    {
        var references = new List<BlobReference>();
        var document = StepEvidenceCodec.Read(_blobs, binding, artifact);
        _ = ReadDocument(binding, artifact, document.Stage == "collected", context, verifyContents: false, 0, references);
        return references.Distinct().ToArray();
    }

    internal (ExecutionPlan Total, StepWriterState Job, ProcessIdentity? Root) Context(StepEvidenceBinding binding, string role = "process")
    {
        RequireContextBinding(binding);
        var journal = new RecoveryJournalRegistry(database).ReadJournal(binding.ParentJournalId);
        if (journal.Kind != JournalKind.RunAttempt || journal.OperationId != binding.ParentOperationId || journal.PlanDigest != binding.PlanDigest) throw Invalid();
        var attempt = ReadPlan<RunAttemptPlan>(AttemptPreparedBinding.Reference(instance, journal), "run-attempt-plan.schema.json");
        var total = ReadPlan<ExecutionPlan>(AttemptPlanCodec.Reference(attempt.ExecutionPlan), "execution-plan.schema.json");
        var latest = new JournalStore(instance, contracts).Read(binding.ParentJournalId);
        return ContextFromValidatedRead(binding, journal, attempt, total, latest, role);
    }

    internal (ExecutionPlan Total, StepWriterState Job, ProcessIdentity? Root) ContextFromValidatedRead(
        StepEvidenceBinding binding, RegisteredJournal journal, RunAttemptPlan attempt, ExecutionPlan total,
        JournalDocument latest, string role = "process")
    {
        RequireContextBinding(binding);
        if (journal.JournalId != binding.ParentJournalId || journal.Kind != JournalKind.RunAttempt ||
            journal.OperationId != binding.ParentOperationId || journal.PlanDigest != binding.PlanDigest ||
            latest.JournalId != binding.ParentJournalId || latest.Kind != JournalKind.RunAttempt ||
            latest.PlanDigest != binding.PlanDigest || latest.PlanPath != journal.PlanPath ||
            latest.Root.GetProperty("operationId").GetGuid() != binding.ParentOperationId) throw Invalid();
        if (attempt.InstanceId != binding.InstanceId.ToString("D") || attempt.JournalId != binding.ParentJournalId.ToString("D") ||
            attempt.OperationId != binding.ParentOperationId.ToString("D") || attempt.RunId != binding.RunId.ToString("D") ||
            attempt.AttemptId != binding.AttemptId.ToString("D")) throw Invalid();
        if (total.InstanceId != attempt.InstanceId || total.RunId != attempt.RunId || !total.ResourceKeys.SequenceEqual(attempt.ResourceKeys)) throw Invalid();
        var job = WriterIntentCodec.ReadAllJobs(latest.Root, total).SingleOrDefault(item => item.StepId == binding.StepId && item.Role == role && item.JobName == binding.JobName) ?? throw Invalid();
        if (StepResultCodec.Binding(latest.Root, job) != binding) throw Invalid();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT r.process_id,r.created_file_time,r.image_path,r.image_sha256,r.user_sid,r.elevated " +
            "FROM journal_writer_job_roots r WHERE r.journal_id=$journal AND r.writer_id=$writer AND r.job_name=$job;";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$writer", binding.WriterId.ToString("D")); command.Parameters.AddWithValue("$job", binding.JobName);
        ProcessIdentity? root;
        using (var row = command.ExecuteReader()) root = row.Read() ? new(row.GetInt32(0), DateTime.FromFileTimeUtc(row.GetInt64(1)),
            row.GetString(2), row.GetString(3), row.GetString(4), row.GetBoolean(5)) : null;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM journal_writer_jobs b JOIN operation_journals j USING(journal_id) " +
            "JOIN operations o ON o.operation_id=j.operation_id WHERE b.journal_id=$journal AND b.writer_id=$writer AND b.job_name=$job " +
            "AND b.session_id=$session AND o.operation_kind='execution.start' AND o.initial_journal_id=j.journal_id);";
        command.Parameters.AddWithValue("$session", binding.SessionId);
        if ((long)command.ExecuteScalar()! != 1) throw Invalid();
        var step = total.Steps.Single(item => item.StepId == binding.StepId);
        var (image, digest) = ExecutionBindingRoles.Image(step, role);
        if (root is not null && (!StringComparer.OrdinalIgnoreCase.Equals(root.ImagePath, image) || root.ImageSha256 != digest)) throw Invalid();
        return (total, job, root);
    }

    internal BlobReference ClosureReference(StepEvidenceBinding binding)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT plan_path,plan_digest,plan_size FROM journal_writer_job_closures WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job;";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$writer", binding.WriterId.ToString("D")); command.Parameters.AddWithValue("$job", binding.JobName);
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetInt64(2), row.GetString(1)) : throw new IOException("writer_job_closure_missing");
    }

    internal ExecutionCancellationIntent? AcceptedClose(StepEvidenceBinding binding)
    {
        _ = Context(binding);
        var parent = new RecoveryJournalRegistry(database).ReadJournal(binding.ParentJournalId);
        var attempt = ReadPlan<RunAttemptPlan>(AttemptPreparedBinding.Reference(instance, parent), "run-attempt-plan.schema.json");
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = ExecutionCancellationRows.Read(connection, transaction, parent, attempt);
        transaction.Commit(); return result;
    }

    internal AutomationJudgement Judgement(ExecutionPlan total, string stepId)
    {
        if (total.Steps.Single(step => step.StepId == stepId).Purpose == "initialization")
            return new(AutomationJudgementLanguage.Keywords, [], [], null);
        var value = Script(total, stepId).Judgement;
        return new(Enum.Parse<AutomationJudgementLanguage>(value.Language, true), value.SuccessKeywords.ToImmutableArray(),
            value.FailureKeywords.ToImmutableArray(), value.Script);
    }

    internal AutomationLimits Limits(ExecutionPlan total, string stepId)
    {
        var value = Script(total, stepId).Limits;
        return new(checked((int)value.MaximumAttempts), checked((int)value.StallTimeoutSeconds), checked((int)value.TotalTimeoutSeconds));
    }

    private AutomationScriptScript Script(ExecutionPlan total, string stepId)
    {
        var content = total.DefinitionBundle;
        var bundle = ReadPlan<AutomationBundle>(new(content.RelativePath, content.SizeBytes, content.Sha256), "automation-bundle.schema.json", content: true);
        var scriptId = total.Steps.Single(item => item.StepId == stepId).ScriptId;
        return bundle.Scripts.Single(item => item.Id == scriptId);
    }

    private StepEvidenceDocument ReadDocument(StepEvidenceBinding binding, BlobReference artifact, bool collected,
        (ExecutionPlan Total, StepWriterState Job, ProcessIdentity? Root) context, bool verifyContents, int depth,
        List<BlobReference>? references = null, VerifiedRecordRetirement? retirement = null)
    {
        if (depth > 2) throw Invalid();
        var document = StepEvidenceCodec.Read(_blobs, binding, artifact);
        if ((document.Stage == "collected") != collected || document.Root != context.Root ||
            document.Stage == "observed" && document.Business == BusinessOutcome.Unverified && document.Engine is EngineOutcome.Running or EngineOutcome.Unknown)
            throw Invalid();
        if (collected)
        {
            var closure = new JobClosureStore(instance, database, contracts).ReadForExecutionEvidence(binding.ParentJournalId,
                binding.WriterId, binding.JobName, binding.SessionId) ?? throw Invalid();
            if (document.Closure != ClosureReference(binding) || closure.InstanceId != binding.InstanceId || closure.OperationId != binding.ParentOperationId ||
                closure.Root != context.Root || closure.RunElapsedMilliseconds != context.Job.ClosedElapsedMilliseconds ||
                context.Job.ClosedElapsedMilliseconds is null || (document.Engine == EngineOutcome.NotStarted) != !closure.WasExecuted) throw Invalid();
        }
        StepEvidenceDocument? prior = null;
        if (document.PreviousObservation is { } previous)
        {
            prior = ReadDocument(binding, previous, false, context, verifyContents, depth + 1, references, retirement);
            if (document.Engine != prior.Engine && prior.Engine is not (EngineOutcome.Running or EngineOutcome.Unknown) ||
                document.Business != prior.Business && prior.Business != BusinessOutcome.Unverified) throw Invalid();
        }
        RawOutputManifest? raw = null;
        if (document.RawOutput is { } output)
        {
            raw = RawOutputCodec.Read(_blobs, binding, output);
            var baseline = verifyContents ? FileLogEvidence.Require(instance, contracts, _blobs, context.Total, binding, context.Job.FileLogBaseline, raw) :
                raw.FileLogBaseline is { } baselineReference && baselineReference == context.Job.FileLogBaseline
                    ? LogBaselineCodec.Read(_blobs, baselineReference, binding.ParentJournalId, binding.StepId, binding.JobName,
                        context.Total.Steps.Single(step => step.StepId == binding.StepId).LogTargets) : null;
            if (raw.FileLogBaseline != context.Job.FileLogBaseline) throw Invalid();
            if (collected && !raw.IsFinal) throw Invalid();
            references?.AddRange(FileLogEvidence.References(_blobs, raw));
            if (baseline is not null) references?.AddRange(baseline.Candidates.Select(candidate => candidate.Checkpoint).OfType<BlobReference>());
        }
        if (document.Stop is { } stop)
        {
            if (context.Job.ClosedElapsedMilliseconds is null || stop.ElapsedMilliseconds > context.Job.ClosedElapsedMilliseconds ||
                raw is null || stop.ObservedBytes > raw.ObservedBytes) throw Invalid();
            if (verifyContents) VerifyStop(context.Total, binding.StepId, document, stop);
        }
        if (document.ProviderControl is { } providerControl)
        {
            var reader = new ProviderControlReader(instance, database, contracts);
            if (verifyContents) _ = reader.Read(binding, providerControl);
            references?.AddRange(reader.ReadReferences(binding, providerControl));
        }
        else if (context.Total.Steps.Single(step => step.StepId == binding.StepId).Provider is not null &&
            document.Root is not null && document.Stage == "collected")
        {
            var published = ProviderTerminalCodec.Read(new JournalStore(instance, contracts).Read(binding.ParentJournalId).Root, context.Total)
                .SingleOrDefault(item => item.Binding == binding);
            if (published is not null) throw Invalid();
        }
        if (document.TaskAdapter is { } adapter)
        {
            var step = context.Total.Steps.Single(item => item.StepId == binding.StepId);
            var observed = AdapterObservationCodec.Read(_blobs, binding, document.RawOutput ?? throw Invalid(), step, adapter);
            TaskRetryEvidence.RequireInvocation(instance, contracts, context.Total, observed);
            references?.Add(adapter);
            references?.AddRange(AdapterObservationCodec.References(_blobs, step, observed));
        }
        if (document.WorkspaceOutput is { } workspaceOutput)
        {
            var outputReferences = ProviderOutputCodec.Read(instance, database, contracts, binding, workspaceOutput,
                document.ProviderControl ?? throw Invalid(), document.Closure ?? throw Invalid(), verifyContents);
            references?.AddRange(outputReferences);
        }
        ExecutionJudgementEvidence? scriptEvidence = null;
        if (document.Judgement is { } judgementReference)
        {
            scriptEvidence = new JudgementEvidenceReader(instance, database, contracts).Read(context.Total, binding, judgementReference, collected);
            if (document.Generation != scriptEvidence.Exchanges.Length) throw Invalid();
            references?.Add(judgementReference);
            references?.AddRange(JudgementEvidenceCodec.References(_blobs, scriptEvidence));
        }
        if (!verifyContents) return document;
        var definition = Judgement(context.Total, binding.StepId);
        if (definition.Language != AutomationJudgementLanguage.Keywords)
        {
            if (document.Business == BusinessOutcome.NotApplicable) throw Invalid();
            var decision = scriptEvidence is null ? null : new JudgementEvidenceReader(instance, database, contracts)
                .Replay(context.Total, scriptEvidence, definition, retirement, document.Judgement);
            if (document.Business != BusinessOutcome.Unverified && prior?.Business != document.Business &&
                (decision is null || decision.Outcome != document.Business ||
                    decision.ReasonCode != document.ReasonCode && document.ReasonCode != document.Stop?.ReasonCode)) throw Invalid();
            return document;
        }
        if (scriptEvidence is not null || document.Generation != 0) throw Invalid();
        var judgement = new AttemptBusinessJudgement(binding.AttemptId, definition);
        var retiredRaw = raw is not null && document.RawOutput is { } outputReference &&
            retirement?.ReplacesRawReplay(binding, outputReference) == true;
        var continuous = raw is not null && !retiredRaw && RawOutputReplay.Read(_blobs, raw, line => judgement.ObserveLine(judgement.InputIdentity, line));
        if (judgement.Mode == ExecutionJudgementMode.None && document.TaskAdapter is { } adapterReference &&
            document.Business != BusinessOutcome.Unverified && prior?.Business != document.Business)
        {
            var step = context.Total.Steps.Single(item => item.StepId == binding.StepId);
            var observed = AdapterObservationCodec.Read(_blobs, binding, document.RawOutput ?? throw Invalid(), step, adapterReference);
            var decision = AdapterBusinessPolicy.Decide(step.Tasks.Select(TaskAdapterProtocol.ReadTask).ToImmutableArray(), observed.Observations);
            if (document.Root is null || document.Stage != "collected" || document.Business != decision.Outcome ||
                document.ReasonCode != decision.ReasonCode && document.ReasonCode != document.Stop?.ReasonCode) throw Invalid();
            return document;
        }
        if (document.Business == BusinessOutcome.NotApplicable)
        {
            if (judgement.Mode != ExecutionJudgementMode.None || document.Root is null || document.Stage != "collected" && document.ExitCode is null ||
                context.Total.Steps.Single(item => item.StepId == binding.StepId) is { Adapter: not null, Purpose: not "initialization" } ||
                document.ReasonCode != "execution_judgement_not_applicable" && document.ReasonCode != document.Stop?.ReasonCode) throw Invalid();
            return document;
        }
        if (document.Business != BusinessOutcome.Unverified && prior?.Business != document.Business &&
            !retiredRaw && (!continuous || judgement.Decision is null || judgement.Business != document.Business || judgement.Decision.ReasonCode != document.ReasonCode)) throw Invalid();
        if (document.Business == BusinessOutcome.Partial && prior?.Business != BusinessOutcome.Partial) throw Invalid();
        return document;
    }

    private void VerifyStop(ExecutionPlan total, string stepId, StepEvidenceDocument document, StepStopObservation stop)
    {
        var elapsed = TimeSpan.FromMilliseconds(stop.ElapsedMilliseconds);
        var idle = elapsed - TimeSpan.FromMilliseconds(stop.LastOutputElapsedMilliseconds);
        var business = stop.BusinessAcceptedElapsedMilliseconds.HasValue ? document.Business : (BusinessOutcome?)null;
        if (business is not (null or BusinessOutcome.Succeeded or BusinessOutcome.Partial or BusinessOutcome.Failed)) throw Invalid();
        var sinceBusiness = stop.BusinessAcceptedElapsedMilliseconds is { } accepted ? elapsed - TimeSpan.FromMilliseconds(accepted) : (TimeSpan?)null;
        var cause = StepStopPolicy.Evaluate(Limits(total, stepId), elapsed, idle, business, sinceBusiness, observeStall: true);
        if (cause == StepStopCause.Continue || StepStopProof.Reason(cause) != stop.ReasonCode) throw Invalid();
    }

    private T ReadPlan<T>(BlobReference reference, string schema, bool content = false)
    {
        if (!content) StepEvidenceCodec.Plan(reference);
        if (reference.SizeBytes is < 1 or > StrictJson.MaximumBytes) throw Invalid();
        using var input = _blobs.OpenVerified(reference); var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        return contracts.Read<T>(bytes, schema);
    }

    private void RequireContextBinding(StepEvidenceBinding binding)
    {
        if (binding.InstanceId != instance.InstanceId || binding.ParentJournalId == Guid.Empty || binding.ParentOperationId == Guid.Empty ||
            binding.RunId == Guid.Empty || binding.AttemptId == Guid.Empty) throw Invalid();
    }

    private static IOException Invalid() => new("execution_step_evidence_unbound");
}
