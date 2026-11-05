using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class StepWriterScope : IConfigurationWriterScope
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly Guid _journalId;
    private readonly string _stepId;
    private readonly string _role;
    private readonly Guid? _judgementOwnerId;
    private readonly Guid _captureRunId;
    private readonly string _capturePlanDigest;
    private readonly int _captureAttemptOrdinal;
    private readonly int _captureSessionId;
    private string? _jobName;
    private StepWriterState? _closedStep;
    private readonly ExecutionProviderRegistry.PreparedExecutionProvider? _provider;
    public Guid OperationId { get; }
    public Guid ProcessScopeId { get; }
    public ImmutableArray<Guid> JournalIds { get; }
    public ConfigurationEditorCommand Command { get; }
    public CommandLineSyntax CommandLineSyntax { get; }
    internal string Role => _role;
    internal string StepId => _stepId;
    internal Guid? JudgementOwnerId => _judgementOwnerId;

    internal StepWriterScope(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        Guid parentJournalId, string stepId, string role = "process", Guid? judgementOwnerId = null, ExecutionProviderRegistry.PreparedExecutionProvider? provider = null)
    {
        lease.VerifyHeld(instance);
        _instance = instance; _database = database; _contracts = contracts; _lease = lease; _journalId = parentJournalId; _stepId = stepId;
        if (role is not ("preHook" or "process" or "judgement" or "postHook") || (role == "judgement") != judgementOwnerId.HasValue || judgementOwnerId == Guid.Empty)
            throw new IOException("execution_writer_role_unbound");
        _role = role; _judgementOwnerId = judgementOwnerId; _provider = provider;
        var state = Read();
        _captureRunId = Guid.Parse(state.Attempt.RunId); _capturePlanDigest = state.Journal.PlanDigest;
        _captureAttemptOrdinal = checked((int)state.Attempt.AttemptOrdinal);
        using var ownerProcess = System.Diagnostics.Process.GetCurrentProcess(); _captureSessionId = ownerProcess.SessionId;
        RequireAdmission(state);
        var step = state.Total.Steps.SingleOrDefault(step => step.StepId == stepId) ?? throw new IOException("execution_writer_step_unbound");
        if (step.EngineKind is not ("process" or "extensionWorker") || (step.EngineKind == "extensionWorker") != (_provider is not null))
            throw new IOException("execution_writer_engine_unavailable");
        RequireProvider(step);
        OperationId = state.Journal.OperationId; ProcessScopeId = Guid.Parse(state.Attempt.AttemptId); JournalIds = [parentJournalId];
        Command = role == "process" ? EditorCommandPolicy.Freeze(new(step.ExecutablePath, step.ImageSha256, step.Arguments.ToImmutableArray(), step.WorkingDirectory) { ExclusiveProgram = step.ExclusiveProgram == true }) :
            role == "judgement" ? ExecutionJudgementInputs.Command(state.Total, stepId) :
            HookCommand(step, role);
        CommandLineSyntax = ExecutionBindingRoles.IsHook(role) && ExecutionBindingRoles.Hook(step, role).LaunchKind == "command"
            ? CommandLineSyntax.CommandInterpreter : CommandLineSyntax.ArgumentVector;
        _ = new JournalWriterStore(database).RequireRegisteredExecutor(parentJournalId);
    }

    public void RequirePrepared() => RequirePrepared(Read());

    private void RequirePrepared(AttemptWriterState state)
    {
        if (_jobName is null) { RequireAdmission(state); return; }
        var step = RequiredStep(state);
        if (state.IsDispatchClosed || state.Latest.Revision != state.Journal.Revision || step.ClosedElapsedMilliseconds is not null ||
            state.Journal.Phase is not (JournalPhase.Prepared or JournalPhase.Applying) ||
            state.Gate.State != (state.Journal.Phase == JournalPhase.Prepared ? EffectGateState.Unentered : EffectGateState.Entered))
            throw new IOException("execution_writer_registration_closed");
        var ordinal = state.Total.Steps.ToList().FindIndex(item => item.StepId == _stepId);
        if (BindingSkipCodec.Processed(state.Latest.Root, state.Total, state.Jobs).Length != ordinal + 1) throw new IOException("execution_writer_job_unbound");
        using var prior = ExecutionStepAdmission.HoldPredecessors(_instance, _database, _contracts, state, ordinal);
    }

    public void RegisterJob(OwnedWorkerJob job)
        => _lease.PublishExecution(() => RegisterJobCore(job));

    private void RegisterJobCore(OwnedWorkerJob job)
    {
        var state = Read(); RequirePrepared(state);
        if (job.GetAccounting().TotalProcesses != 0 || _jobName is not null && _jobName != job.Name) throw new IOException("execution_writer_job_unbound");
        var writer = JournalWriterStore.CurrentWriter;
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var item = new StepWriterIntent(WriterIntentCodec.Format, 1, _stepId, "registered", job.Name, writer.WriterId, current.SessionId, null)
            { Role = _role, JudgementOwnerId = _judgementOwnerId };
        var existing = state.Jobs.SingleOrDefault(step => step.StepId == _stepId && step.Role == _role);
        if (existing is null)
        {
            var ordinal = ExecutionStepAdmission.RequireNext(state.Latest, state.Total, _stepId, _role);
            using var prior = ExecutionStepAdmission.HoldPredecessors(_instance, _database, _contracts, state, ordinal);
            var held = new List<IDisposable>();
            try
            {
                if (_role == "process" && state.Total.Steps.Single(step => step.StepId == _stepId).LogTargets.Count != 0)
                    item = item with { FileLogBaseline = LogBaselineCodec.Capture(new(_instance, _contracts), _journalId,
                        _stepId, job.Name, ExecutionLogSelectors.Read(_instance, _contracts, state.Total, _stepId), held) };
                _jobName = job.Name; Append(state, item, state.Latest.Phase);
            }
            finally { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); }
        }
        else if (existing.JobName != job.Name || existing.WriterId != writer.WriterId || existing.SessionId != current.SessionId || existing.ClosedElapsedMilliseconds is not null)
            throw new IOException("execution_writer_job_unbound");
        Synchronize();
    }

    internal void EnterApplying()
        => _lease.PublishExecution(EnterApplyingCore);

    private void EnterApplyingCore()
    {
        if (_role is not ("process" or "preHook")) throw new IOException("execution_effect_owner_unbound");
        var state = Read();
        ExecutionStepAdmission.RequireOpenRoles(state.Total, state.Jobs, _stepId, _role, state.Latest.Root);
        if (state.IsDispatchClosed || state.Journal.OperationId != OperationId || state.Jobs.Length == 0 || state.Jobs[^1].StepId != _stepId)
            throw new IOException("execution_writer_steps_unprepared");
        if (state.Journal.Phase == JournalPhase.Applying && state.Gate.State == EffectGateState.Entered && state.Latest.Revision == state.Journal.Revision) return;
        if (state.Latest.Phase == JournalPhase.Prepared && state.Latest.Revision != state.Journal.Revision)
            throw new IOException("execution_writer_steps_unprepared");
        if (ExecutionBindingRoles.DispatchedSteps(state.Jobs).Length != 1 || state.Jobs[0].StepId != _stepId) throw new IOException("execution_writer_steps_unprepared");
        if (state.Jobs.Any(step => step.ClosedElapsedMilliseconds is not null)) throw new IOException("execution_writer_effect_entry_closed");
        if (state.Journal.Phase != JournalPhase.Prepared || state.Latest.Phase is not (JournalPhase.Prepared or JournalPhase.Applying))
            throw new IOException("execution_writer_effect_entry_closed");
        if (state.Gate.State == EffectGateState.Unentered) new JournalEffectGate(_database).EnterExecution(state, _stepId, _role);
        else if (state.Gate.State != EffectGateState.Entered) throw new IOException("execution_writer_effect_entry_closed");
        state = Read();
        if (state.Latest.Phase == JournalPhase.Prepared) Append(state, null, JournalPhase.Applying);
        Synchronize();
    }

    public void RequireStart()
    {
        var state = Read(); var step = RequiredStep(state);
        RequireProvider(state.Total.Steps.Single(value => value.StepId == _stepId));
        if (state.IsDispatchClosed || state.Journal.Phase != JournalPhase.Applying || state.Latest.Revision != state.Journal.Revision || state.Gate.State != EffectGateState.Entered ||
            step.ClosedElapsedMilliseconds is not null) throw new IOException("execution_writer_start_closed");
        new JobClosureStore(_instance, _database, _contracts).RequireAttemptStart(state, step);
    }

    public void RecordRoot(string jobName, ProcessIdentity root)
    {
        RequireStart(); var state = Read(); var step = RequiredStep(state);
        if (jobName != step.JobName) throw new IOException("execution_writer_job_unbound");
        new JobClosureStore(_instance, _database, _contracts).RecordAttemptRoot(state, step, root);
    }

    private void RequireProvider(NexusPipeline.Platform.Serialization.Protocol.ExecutionPlanStep step)
    {
        if (_provider is null) return;
        _provider.Use(package =>
        {
            if (step.Provider is null || _provider.ExtensionId != step.Provider.ExtensionId ||
                _provider.ActivationId.ToString("D") != step.Provider.ActivationId ||
                package.Declaration.EntryId != step.Provider.EntryId || package.ManifestSha256 != step.Provider.Manifest.Sha256)
                throw new IOException("execution_provider_activation_unbound");
            return true;
        });
    }

    internal void RequireProviderBootstrap(StepEvidenceBinding binding)
    {
        var state = Read(); var job = RequiredStep(state);
        if (_role != "process" || _provider is null || state.IsDispatchClosed || state.Journal.Phase != JournalPhase.Applying ||
            state.Latest.Revision != state.Journal.Revision || job.ClosedElapsedMilliseconds.HasValue ||
            StepResultCodec.Binding(state.Latest.Root, job) != binding)
            throw new IOException("execution_provider_admission_closed");
        _provider.Use(_ => _database.Write((connection, transaction) =>
        {
            RequireProvider(state.Total.Steps.Single(step => step.StepId == _stepId));
            AttemptWriterState.RequireCursor(connection, transaction, state.Journal, EffectGateState.Entered);
            ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, _journalId);
            ExecutionStepAdmission.RequireJobsInside(connection, transaction, _journalId, state.AllJobs);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM journal_writer_job_roots WHERE journal_id=$journal " +
                "AND writer_id=$writer AND job_name=$job) AND NOT EXISTS(SELECT 1 FROM journal_writer_job_closures " +
                "WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job);";
            command.Parameters.AddWithValue("$journal", _journalId.ToString("D"));
            command.Parameters.AddWithValue("$writer", job.WriterId.ToString("D")); command.Parameters.AddWithValue("$job", job.JobName);
            if ((long)command.ExecuteScalar()! != 1) throw new IOException("execution_provider_admission_closed");
            return true;
        }));
    }

    internal void RequireJudgementInvocation()
    {
        var state = Read(); var job = RequiredStep(state);
        if (_role != "judgement" || state.IsDispatchClosed || state.Journal.Phase != JournalPhase.Applying ||
            state.Latest.Revision != state.Journal.Revision || job.ClosedElapsedMilliseconds.HasValue ||
            state.Steps.Length == 0 || state.Steps[^1].StepId != _stepId ||
            StepResultCodec.Read(state.Latest.Root, state.Total).Any(result => result.Binding.StepId == _stepId && result.IsCollected))
            throw new IOException("judgement_invocation_admission_closed");
        ExecutionStepAdmission.RequireRequiredRoles(state.Total, state.Jobs, _stepId, "process", state.Latest.Root);
        var primary = state.Steps.Single(step => step.StepId == _stepId);
        _database.Write((connection, transaction) =>
        {
            AttemptWriterState.RequireCursor(connection, transaction, state.Journal, EffectGateState.Entered);
            ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, _journalId);
            ExecutionStepAdmission.RequireJobsInside(connection, transaction, _journalId, state.AllJobs);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM journal_writer_job_roots WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job) " +
                "AND EXISTS(SELECT 1 FROM journal_writer_job_roots WHERE journal_id=$journal AND writer_id=$primaryWriter AND job_name=$primaryJob) " +
                "AND NOT EXISTS(SELECT 1 FROM journal_writer_job_closures WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job);";
            command.Parameters.AddWithValue("$journal", _journalId.ToString("D")); command.Parameters.AddWithValue("$writer", job.WriterId.ToString("D"));
            command.Parameters.AddWithValue("$job", job.JobName); command.Parameters.AddWithValue("$primaryWriter", primary.WriterId.ToString("D"));
            command.Parameters.AddWithValue("$primaryJob", primary.JobName);
            if ((long)command.ExecuteScalar()! != 1) throw new IOException("judgement_invocation_admission_closed");
            return true;
        });
    }

    public void CloseAdmission(long elapsedMilliseconds)
        => _lease.PublishExecution(() => CloseAdmissionCore(elapsedMilliseconds));

    private void CloseAdmissionCore(long elapsedMilliseconds)
    {
        if (elapsedMilliseconds is < 0 or > 922337203685477) throw new IOException("execution_writer_elapsed_invalid");
        var state = Read(); var step = RequiredStep(state);
        if (state.Journal.Phase is not (JournalPhase.Prepared or JournalPhase.Applying) || state.Gate.State != EffectGateState.Entered && elapsedMilliseconds != 0)
            throw new IOException("execution_writer_close_unbound");
        if (step.ClosedElapsedMilliseconds is { } closed)
        {
            if (closed != elapsedMilliseconds) throw new IOException("execution_writer_elapsed_changed");
        }
        else Append(state, new StepWriterIntent(WriterIntentCodec.Format, 1, _stepId, "closed", step.JobName, step.WriterId, step.SessionId, elapsedMilliseconds)
            { Role = _role, JudgementOwnerId = _judgementOwnerId, FileLogBaseline = step.FileLogBaseline }, state.Latest.Phase);
        Synchronize();
        _closedStep = step with { ClosedElapsedMilliseconds = elapsedMilliseconds };
    }

    public FrozenJobClosure BindClosure(FrozenJobClosure closure, long elapsedMilliseconds)
    {
        _lease.VerifyHeld(_instance);
        var step = _closedStep ?? throw new IOException("execution_writer_closure_unbound");
        if (step.ClosedElapsedMilliseconds != elapsedMilliseconds || closure.JobName != step.JobName || closure.WriterId != step.WriterId ||
            closure.SessionId != step.SessionId || closure.InstanceId != _instance.InstanceId || !closure.WasExecuted && elapsedMilliseconds != 0)
            throw new IOException("execution_writer_closure_unbound");
        return closure with { OperationId = OperationId, JournalIds = JournalIds, RunElapsedMilliseconds = elapsedMilliseconds };
    }

    public BlobReference RecordClosed(FrozenJobClosure closure)
    {
        _lease.VerifyHeld(_instance);
        var step = _closedStep ?? throw new IOException("execution_writer_closure_unbound");
        if (closure.OperationId != OperationId || !closure.JournalIds.SequenceEqual(JournalIds) ||
            closure.JobName != step.JobName || closure.WriterId != step.WriterId || closure.SessionId != step.SessionId ||
            closure.RunElapsedMilliseconds != step.ClosedElapsedMilliseconds) throw new IOException("execution_writer_closure_unbound");
        // RecordClosed rereads the complete attempt binding before persisting or admitting its SQL closure.
        return new JobClosureStore(_instance, _database, _contracts).RecordClosed(closure);
    }

    internal void RequireUnregisteredJob(string jobName)
    {
        var state = Read();
        if (state.Jobs.Any(job => job.JobName == jobName)) throw new IOException("execution_unregistered_job_release_unverified");
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        AttemptWriterState.RequireCursor(connection, transaction, state.Journal, state.Gate.State);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM journal_writer_jobs WHERE job_name=$job) OR " +
            "EXISTS(SELECT 1 FROM journal_writer_job_roots WHERE job_name=$job) OR " +
            "EXISTS(SELECT 1 FROM journal_writer_job_closures WHERE job_name=$job);";
        command.Parameters.AddWithValue("$job", jobName);
        if ((long)command.ExecuteScalar()! != 0) throw new IOException("execution_unregistered_job_release_unverified");
    }

    internal void RequireCaptureParent(StepEvidenceBinding binding, OwnedWorkerIdentity native)
    {
        _lease.VerifyHeld(_instance);
        var writer = JournalWriterStore.CurrentWriter;
        if (_role != "process" || _jobName is null || binding.SessionId != _captureSessionId || binding.InstanceId != _instance.InstanceId || binding.ParentJournalId != _journalId ||
            binding.ParentOperationId != OperationId || binding.RunId != _captureRunId || binding.AttemptId != ProcessScopeId ||
            binding.StepId != _stepId || binding.JobName != _jobName || binding.WriterId != writer.WriterId ||
            binding.PlanDigest != _capturePlanDigest || native.InstanceId != _instance.InstanceId || native.AttemptId != ProcessScopeId ||
            !StringComparer.OrdinalIgnoreCase.Equals(native.ImagePath, Command.ExecutablePath) || native.ImageSha256 != Command.ExecutableSha256)
            throw new IOException("execution_capture_writer_unbound");
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, _journalId);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT r.process_id,r.created_file_time,r.image_path,r.image_sha256,r.user_sid,r.elevated," +
            "w.process_id,w.created_file_time,w.image_path,w.image_sha256,w.user_sid,w.elevated FROM journal_writer_jobs b " +
            "JOIN journal_writers w USING(journal_id,writer_id) JOIN journal_writer_job_roots r USING(journal_id,writer_id,job_name) " +
            "JOIN operation_journals j ON j.journal_id=b.journal_id JOIN operations o ON o.operation_id=j.operation_id " +
            "JOIN journal_effect_gates g ON g.journal_id=j.journal_id WHERE b.journal_id=$journal AND b.writer_id=$writer " +
            "AND b.job_name=$job AND b.session_id=$session AND j.operation_id=$operation AND j.operation_kind='runAttempt' " +
            "AND j.phase='applying' AND g.state='entered' AND o.operation_kind='execution.start' AND o.initial_journal_id=j.journal_id " +
            "AND j.plan_digest=$digest AND j.plan_path=$plan AND NOT EXISTS(SELECT 1 FROM journal_writer_job_closures c " +
            "WHERE c.journal_id=b.journal_id AND c.writer_id=b.writer_id AND c.job_name=b.job_name) " +
            "AND NOT EXISTS(SELECT 1 FROM execution_start_sources s WHERE s.operation_id=o.operation_id AND " +
            "(s.journal_id<>j.journal_id OR s.run_id<>$run OR s.attempt_ordinal<>$ordinal OR s.subject_id<>o.subject_id));";
        command.Parameters.AddWithValue("$journal", _journalId.ToString("D"));
        command.Parameters.AddWithValue("$writer", writer.WriterId.ToString("D"));
        command.Parameters.AddWithValue("$job", _jobName); command.Parameters.AddWithValue("$session", binding.SessionId);
        command.Parameters.AddWithValue("$operation", OperationId.ToString("D"));
        command.Parameters.AddWithValue("$digest", _capturePlanDigest);
        command.Parameters.AddWithValue("$plan", $"blobs/plans/{_capturePlanDigest}.json");
        command.Parameters.AddWithValue("$run", _captureRunId.ToString("D")); command.Parameters.AddWithValue("$ordinal", _captureAttemptOrdinal);
        using var row = command.ExecuteReader();
        var actualRoot = new ProcessIdentity(native.ProcessId, native.CreatedAtUtc, native.ImagePath, native.ImageSha256, native.UserSid, native.IsElevated);
        if (!row.Read() || ReadCaptureProcess(row, 0) != actualRoot || ReadCaptureProcess(row, 6) != writer.Process || row.Read())
            throw new IOException("execution_capture_writer_unbound");
        transaction.Commit();
        // Parent handles grant no effect: the capture store still validates complete current evidence before resume or ACK.
    }

    private static ProcessIdentity ReadCaptureProcess(Microsoft.Data.Sqlite.SqliteDataReader row, int offset) =>
        new(row.GetInt32(offset), DateTime.FromFileTimeUtc(row.GetInt64(offset + 1)), row.GetString(offset + 2),
            row.GetString(offset + 3), row.GetString(offset + 4), row.GetBoolean(offset + 5));

    internal AttemptWriterState VerifyReadOnly() => Read();
    private static ConfigurationEditorCommand HookCommand(NexusPipeline.Platform.Serialization.Protocol.ExecutionPlanStep step, string role)
    {
        var hook = ExecutionBindingRoles.Hook(step, role);
        if (hook.LaunchKind is not ("native" or "command" or "powershell")) throw new IOException("execution_hook_runtime_unavailable");
        return EditorCommandPolicy.Freeze(new(hook.Command.Path, hook.Command.Sha256,
            hook.Arguments.ToImmutableArray(), hook.WorkingDirectory));
    }

    private void RequireAdmission(AttemptWriterState state)
    {
        if (state.IsDispatchClosed || state.Latest.Revision != state.Journal.Revision ||
            state.Gate.State != (state.Journal.Phase == JournalPhase.Prepared ? EffectGateState.Unentered : EffectGateState.Entered))
            throw new IOException("execution_writer_registration_closed");
        var ordinal = ExecutionStepAdmission.RequireNext(state.Latest, state.Total, _stepId, _role);
        using var previous = ExecutionStepAdmission.HoldPredecessors(_instance, _database, _contracts, state, ordinal);
    }
    private AttemptWriterState Read()
    {
        _lease.VerifyHeld(_instance);
        return AttemptWriterState.Read(_instance, _database, _contracts, _journalId);
    }
    private StepWriterState RequiredStep(AttemptWriterState state) => state.Jobs.SingleOrDefault(step => step.StepId == _stepId && step.Role == _role &&
        step.JudgementOwnerId == _judgementOwnerId && step.JobName == _jobName)
        ?? throw new IOException("execution_writer_job_unbound");
    private void Synchronize() => ExecutionAttemptProjection.Synchronize(_instance, _database, _contracts, Read());
    private void Append(AttemptWriterState state, StepWriterIntent? item, JournalPhase phase)
    {
        var document = JsonNode.Parse(state.Latest.Export())!.AsObject();
        document["revision"] = state.Latest.Revision + 1; document["phase"] = phase.ToString().ToLowerInvariant();
        document["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        if (item is not null) document["participants"]!.AsArray().Add(WriterIntentCodec.Participant(item, state.Total.Steps.Single(step => step.StepId == _stepId).ScriptId));
        new JournalStore(_instance, _contracts).Append(_journalId, state.Latest.Revision, JsonSerializer.SerializeToUtf8Bytes(document));
    }
}
