using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class PcStageProjection
{
    internal static void RequireMainStartInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, StepWriterState writer)
    {
        var step = state.Total.Steps.Single(item => item.StepId == writer.StepId);
        if (writer.Role is not ("process" or "judgement") || step.Target?.Kind != "pc") return;
        var stage = PcStageCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .SingleOrDefault(item => item.Binding.StepId == writer.StepId);
        if (stage?.Outcome is not (TargetExecutionOutcome.Skipped or TargetExecutionOutcome.ExternalReady or TargetExecutionOutcome.OwnedReady))
            throw new IOException("execution_target_main_start_unavailable");
        RequireConfigurationInstalledInside(connection, transaction, state, writer.StepId);
    }

    internal static void RequireInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument before, JournalDocument next)
    {
        var old = PcStageCodec.Read(before.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        var current = PcStageCodec.Read(next.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        foreach (var item in current.Skip(old.Length))
        {
            if (!state.IsExecutionStart || state.Gate.State != EffectGateState.Entered ||
                before.Phase != JournalPhase.Applying || next.Phase != JournalPhase.Applying) throw Invalid();
            var main = state.Jobs.Single(job => job.StepId == item.Binding.StepId && job.Role == "process");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT NOT EXISTS(SELECT 1 FROM journal_writer_job_roots WHERE journal_id=$journal AND job_name=$main);";
            command.Parameters.AddWithValue("$journal", item.Binding.ParentJournalId.ToString("D"));
            command.Parameters.AddWithValue("$main", main.JobName);
            if ((long)command.ExecuteScalar()! != 1) throw Invalid();
            if (item.Outcome == TargetExecutionOutcome.StoppedByRequest)
            {
                var close = ExecutionCancellationRows.Read(connection, transaction, state.Journal, state.Attempt);
                if (close?.ReasonCode != item.ReasonCode) throw Invalid();
            }
        }
    }

    internal static void RequireConfigurationInstalledInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, string stepId)
    {
        var expected = ExecutionInvocationCodec.CurrentPlans(state.Latest.Root, state.Attempt, stepId).ToArray();
        var groups = new List<(Guid Id, string Subject)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT s.session_id,o.subject_id FROM configuration_run_sessions s JOIN operations o ON o.operation_id=s.session_id " +
                "JOIN operations parent ON parent.operation_id=s.owner_operation_id AND parent.subject_id=o.subject_id " +
                "WHERE s.parent_journal_id=$journal AND s.step_id=$step AND s.invocation_ordinal=$invocation ORDER BY s.session_id LIMIT 2;";
            command.Parameters.AddWithValue("$journal", state.Journal.JournalId.ToString("D")); command.Parameters.AddWithValue("$step", stepId);
            command.Parameters.AddWithValue("$invocation", ExecutionInvocationCodec.Ordinal(state.Latest.Root, stepId));
            using var rows = command.ExecuteReader();
            while (rows.Read()) groups.Add((Guid.Parse(rows.GetString(0)), rows.GetString(1)));
        }
        if (expected.Length == 0) { if (groups.Count != 0) throw Invalid(); return; }
        if (groups.Count != 1) throw Invalid();
        var session = ConfigurationRunRows.Read(connection, transaction, groups[0].Id, groups[0].Subject) ?? throw Invalid();
        if (session.OwnerOperationId != state.Journal.OperationId || session.ParentJournalId != state.Journal.JournalId ||
            session.StepId != stepId || session.Phase != ConfigurationRunPhase.Running || session.IsWriterAdmissionClosed ||
            session.Resources.Length != expected.Length ||
            !session.Resources.Select(item => (item.PlanPath, item.PlanDigest)).ToHashSet()
                .SetEquals(expected.Select(item => (item.RelativePath, item.Sha256)))) throw Invalid();
        foreach (var resource in session.Resources)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM operation_journals WHERE journal_id=$journal AND operation_id=$operation " +
                "AND plan_path=$path AND plan_digest=$digest AND phase=$phase);";
            command.Parameters.AddWithValue("$journal", resource.JournalId.ToString("D"));
            command.Parameters.AddWithValue("$operation", resource.OperationId.ToString("D")); command.Parameters.AddWithValue("$path", resource.PlanPath);
            command.Parameters.AddWithValue("$digest", resource.PlanDigest); command.Parameters.AddWithValue("$phase", "applied");
            if ((long)command.ExecuteScalar()! != 1) throw Invalid();
        }
    }

    private static IOException Invalid() => new("execution_pc_target_stage_projection_unbound");
}
