using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class TargetJobProjection
{
    internal static void RequireInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument before, JournalDocument next)
    {
        var previous = TargetJobCodec.ReadHistory(before.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        var current = TargetJobCodec.ReadHistory(next.Root, state.Total, state.Attempt.ExecutionPlan.Sha256);
        foreach (var item in current.Skip(previous.Length))
        {
            if (!state.IsExecutionStart || state.Gate.State != EffectGateState.Entered ||
                next.Phase is not (JournalPhase.Applying or JournalPhase.Quarantined)) throw Invalid();
            if (item.Stage != "registered")
            {
                if (item.Stage is "root" or "ready" or "retentionPrepared" or "retained" or "recoveredRetained" &&
                    TargetRootFacts.ReadInside(connection, transaction, item.Binding) is null) throw Invalid();
                continue;
            }
            if (next.Phase != JournalPhase.Applying || before.Phase != JournalPhase.Applying) throw Invalid();
            ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, item.Binding.ParentJournalId);
            var jobs = WriterIntentCodec.ReadJobs(before.Root, state.Total);
            ExecutionStepAdmission.RequireOpenRoles(state.Total, jobs, item.Binding.StepId, "process", before.Root);
            if (jobs.Any(job => job.JobName == item.Binding.JobName) ||
                StepResultCodec.Read(before.Root, state.Total).Any(result => result.Binding.StepId == item.Binding.StepId)) throw Invalid();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM journal_writers WHERE journal_id=$journal AND writer_id=$writer) " +
                "AND NOT EXISTS(SELECT 1 FROM journal_writer_job_roots WHERE journal_id=$journal AND job_name=$main);";
            command.Parameters.AddWithValue("$journal", item.Binding.ParentJournalId.ToString("D"));
            command.Parameters.AddWithValue("$writer", item.Binding.OwnerWriterId.ToString("D"));
            command.Parameters.AddWithValue("$main", jobs.Single(job => job.StepId == item.Binding.StepId && job.Role == "process").JobName);
            if ((long)command.ExecuteScalar()! != 1) throw Invalid();
            ExecutionStepAdmission.RequirePredecessorsInside(connection, transaction, state, before,
                state.Total.Steps.ToList().FindIndex(step => step.StepId == item.Binding.StepId));
        }
    }

    internal static void RequireResolved(AttemptWriterState state, JournalDocument document, string? stepId = null)
    {
        if (TargetJobCodec.Read(document.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .Any(item => (stepId is null || item.Binding.StepId == stepId) && item.Stage is not ("stopped" or "retained" or "recoveredStopped" or "recoveredRetained")))
            throw new IOException("execution_target_collection_pending");
    }

    private static IOException Invalid() => new("execution_target_stage_unbound");
}
