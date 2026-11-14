using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record AttemptWriterState(RegisteredJournal Journal, RunAttemptPlan Attempt, ExecutionPlan Total,
    JournalDocument Latest, EffectGateSnapshot Gate, ImmutableArray<StepWriterState> Steps)
{
    private AttemptValidatedRead? ValidatedRead { get; init; }
    internal ExecutionCancellationIntent? Cancellation { get; init; }
    internal bool IsDispatchClosed => Cancellation is not null;
    internal bool IsExecutionStart { get; init; }
    internal ImmutableDictionary<string, int> PreviousInvocations { get; init; } = ImmutableDictionary<string, int>.Empty;
    internal int PreviousInvocationCount(string stepId) => PreviousInvocations.TryGetValue(stepId, out var count) ? count : 0;
    internal ImmutableArray<StepWriterState> Jobs => ValidatedRead?.Jobs(this, all: false) ?? WriterIntentCodec.ReadJobs(Latest.Root, Total);
    internal ImmutableArray<StepWriterState> AllJobs => ValidatedRead?.Jobs(this, all: true) ?? WriterIntentCodec.ReadAllJobs(Latest.Root, Total);
    internal ImmutableArray<ExecutionStepCompletion> Coverage => ValidatedRead?.Coverage(this) ?? StepResultCodec.Coverage(Latest.Root, Total);
    internal static AttemptWriterState Read(RuntimeInstance instance, StateDatabase database, ContractReader contracts, Guid journalId)
    {
        using var reads = OperatingSystem.IsWindows() ? DatabaseReadScope.Hold(database.DatabasePath) : null;
        using var paths = OperatingSystem.IsWindows() ? DirectoryAttributeScope.Hold(instance) : null;
        using var blobs = OperatingSystem.IsWindows() ? BlobReadScope.Hold(instance) : null;
        JournalReadScope.SelectParent(instance, contracts, journalId);
        var journals = database.AttemptReads?.Find(journalId)?.Journals ?? new JournalStore(instance, contracts);
        using var historyLease = OperatingSystem.IsWindows() ? JournalReadScope.Hold(journals, journalId) : null;
        var bound = AttemptPreparedBinding.Read(instance, contracts, database, journalId, journals);
        var history = journals.ReadAllRevisions(journalId);
        var latest = history[^1];
        if (!latest.HasSameBytes(bound.ValidatedRead.History[^1])) throw new IOException("execution_writer_journal_changed");
        var validated = bound.ValidatedRead.WithCoherentFreshHistory(history);
        var total = bound.Total;
        return HistoryReadScope.ReadDatabase(database, (connection, transaction) =>
        {
            var journal = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, journalId);
            var projected = history.Single(item => item.Revision == journal.Revision);
            var gate = new JournalEffectGate(database).ReadInside(connection, transaction, journalId);
            if (bound.IsExecutionStart)
                VerifySqlInside(connection, transaction, journal, projected, total, bound.Plan.ExecutionPlan.Sha256, gate);
            return new AttemptWriterState(journal, bound.Plan, total, latest, gate, WriterIntentCodec.Read(latest.Root, total))
            {
                Cancellation = ExecutionCancellationRows.VerifyInside(connection, transaction, journal, bound.Plan, total, latest.Root, projected.Root),
                IsExecutionStart = bound.IsExecutionStart,
                ValidatedRead = validated, PreviousInvocations = validated.PreviousInvocations
            };
        });
    }

    internal AttemptValidatedRead RequireValidatedRead()
    {
        var validated = ValidatedRead ?? throw new IOException("execution_writer_validated_read_missing");
        validated.RequireFor(this); return validated;
    }

    internal static void VerifySql(StateDatabase database, RegisteredJournal journal, JournalDocument projected, ExecutionPlan total,
        string totalSha256, EffectGateSnapshot gate)
        => HistoryReadScope.ReadDatabase(database, (connection, transaction) =>
        {
            VerifySqlInside(connection, transaction, journal, projected, total, totalSha256, gate);
            return true;
        });

    private static void VerifySqlInside(SqliteConnection connection, SqliteTransaction transaction, RegisteredJournal journal,
        JournalDocument projected, ExecutionPlan total, string totalSha256, EffectGateSnapshot gate)
    {
        var steps = WriterIntentCodec.ReadAllJobs(projected.Root, total);
        RequireCursor(connection, transaction, journal, gate.State);
        AndroidControlRows.VerifyInside(connection, transaction, journal.JournalId,
            ExecutionInvocationCodec.Views(projected.Root).SelectMany(view => AndroidControlCodec.Read(view, total, totalSha256))
                .DistinctBy(item => item.Binding.TargetId).ToArray());
        RequireOwnedJobs(projected.Root, total, totalSha256);
        foreach (var domain in PreviewDomainCodec.Read(projected.Root, total, totalSha256))
            PreviewDomainEvidence.RequireSourceInside(connection, transaction, domain.Source);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT b.job_name,b.writer_id,b.session_id,r.image_path,r.image_sha256 " +
            "FROM main.journal_writer_jobs b LEFT JOIN main.journal_writer_job_roots r " +
            "ON r.journal_id=b.journal_id AND r.writer_id=b.writer_id AND r.job_name=b.job_name " +
            "WHERE b.journal_id=$journal ORDER BY b.job_name LIMIT 4097;";
        command.Parameters.AddWithValue("$journal", journal.JournalId.ToString("D"));
        var jobs = new List<(string Job, Guid Writer, int Session)>();
        using (var row = command.ExecuteReader())
        while (row.Read())
        {
            var job = (Job: row.GetString(0), Writer: Guid.Parse(row.GetString(1)), Session: row.GetInt32(2));
            jobs.Add(job);
            var step = steps.SingleOrDefault(step => step.JobName == job.Job && step.WriterId == job.Writer && step.SessionId == job.Session)
                ?? throw new IOException("execution_writer_jobs_unbound");
            if (!row.IsDBNull(3))
            {
                var view = ExecutionInvocationCodec.ForJob(projected.Root, total, step.JobName);
                ExecutionStepAdmission.RequireRequiredRoles(total, WriterIntentCodec.ReadJobs(view, total), step.StepId, step.Role, view);
                var input = total.Steps.Single(item => item.StepId == step.StepId);
                var (image, digest) = ExecutionBindingRoles.Image(input, step.Role);
                if (gate.State != EffectGateState.Entered || !StringComparer.OrdinalIgnoreCase.Equals(row.GetString(3), image) || row.GetString(4) != digest)
                    throw new IOException("execution_writer_root_unbound");
            }
        }
        if (jobs.Count > 1024 || jobs.Count != steps.Length) throw new IOException("execution_writer_jobs_unbound");
    }

    internal static void RequireCursor(SqliteConnection connection, SqliteTransaction transaction, RegisteredJournal expected, EffectGateState gate,
        bool requireExecutionStart = true)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.operation_journals j JOIN main.operations o ON o.operation_id=j.operation_id " +
            "JOIN main.journal_effect_gates g ON g.journal_id=j.journal_id WHERE j.journal_id=$journal AND j.operation_id=$operation " +
            "AND j.operation_kind='runAttempt' AND j.phase=$phase AND j.revision=$revision AND j.plan_path=$path AND j.plan_digest=$digest " +
            "AND ($start=0 OR o.operation_kind='execution.start') AND o.initial_journal_id=j.journal_id AND g.state=$gate);";
        command.Parameters.AddWithValue("$journal", expected.JournalId.ToString("D")); command.Parameters.AddWithValue("$operation", expected.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$phase", expected.Phase.ToString().ToLowerInvariant()); command.Parameters.AddWithValue("$revision", expected.Revision);
        command.Parameters.AddWithValue("$path", expected.PlanPath); command.Parameters.AddWithValue("$digest", expected.PlanDigest);
        command.Parameters.AddWithValue("$gate", gate.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$start", requireExecutionStart);
        if ((long)command.ExecuteScalar()! != 1) throw new IOException("execution_writer_cursor_unbound");
    }

    internal static void RequireOwnedJobs(JsonElement journal, ExecutionPlan total, string totalSha256)
    {
        var names = new Dictionary<string, (string Step, int Ordinal)>(StringComparer.Ordinal);
        foreach (var view in ExecutionInvocationCodec.Views(journal))
        {
            var current = WriterIntentCodec.ReadJobs(view, total).Select(item => (Name: item.JobName, Step: item.StepId))
                .Concat(TargetJobCodec.Read(view, total, totalSha256).Select(item => (item.Binding.JobName, item.Binding.StepId)))
                .Concat(ProviderLauncherCodec.Read(view, total, totalSha256).Select(item => (item.Binding.JobName, item.Binding.StepId)))
                .Concat(AndroidControlCodec.Read(view, total, totalSha256).Select(item => (item.Binding.JobName, item.Binding.StepId)))
                .Concat(ExecutionCaptureCodec.Read(view, total, totalSha256).Select(item => (item.Binding.HelperJobName, item.Binding.Primary.StepId)))
                .Concat(PreviewDomainCodec.Read(view, total, totalSha256).Select(item => (item.Source.JobName, item.Source.Primary.StepId))).ToArray();
            if (current.Select(item => item.Item1).Distinct(StringComparer.Ordinal).Count() != current.Length) throw new IOException("execution_owned_job_capacity_exceeded");
            foreach (var item in current)
            {
                var owner = (item.Item2, ExecutionInvocationCodec.Ordinal(view, item.Item2));
                if (names.TryGetValue(item.Item1, out var old) && old != owner) throw new IOException("execution_owned_job_capacity_exceeded");
                names[item.Item1] = owner;
            }
        }
        if (names.Count > JournalCapacityLimits.MaximumOwnedJobs) throw new IOException("execution_owned_job_capacity_exceeded");
    }

    internal static AttemptWriterState BindClosure(RuntimeInstance instance, StateDatabase database, ContractReader contracts, FrozenJobClosure closure)
    {
        if (closure.InstanceId != instance.InstanceId || closure.JournalIds.Length != 1 || closure.RunElapsedMilliseconds is null)
            throw new IOException("execution_writer_closure_unbound");
        var state = Read(instance, database, contracts, closure.JournalIds[0]);
        state.RequireClosure(closure);
        return state;
    }

    internal void RequireClosure(FrozenJobClosure closure)
    {
        if (closure.InstanceId.ToString("D") != Attempt.InstanceId || closure.JournalIds.Length != 1 ||
            closure.JournalIds[0] != Journal.JournalId || closure.RunElapsedMilliseconds is null)
            throw new IOException("execution_writer_closure_unbound");
        var state = this;
        var projected = RequireValidatedRead().History.Single(item => item.Revision == Journal.Revision);
        var step = WriterIntentCodec.ReadAllJobs(projected.Root, state.Total).SingleOrDefault(item => item.JobName == closure.JobName)
            ?? throw new IOException("execution_writer_closure_unbound");
        if (state.Journal.OperationId != closure.OperationId || step.WriterId != closure.WriterId || step.SessionId != closure.SessionId ||
            step.ClosedElapsedMilliseconds != closure.RunElapsedMilliseconds ||
            closure.WasExecuted && state.Gate.State != EffectGateState.Entered)
            throw new IOException("execution_writer_closure_unbound");
    }
}
