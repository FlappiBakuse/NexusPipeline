using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class RetryHookProjection
{
    internal static void RequireInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument before, JournalDocument after)
    {
        var prior = RetryHookCodec.Read(before.Root, state.Total);
        var added = RetryHookCodec.Read(after.Root, state.Total).Where(item =>
            !prior.Any(old => old.StepId == item.StepId && old.Role == item.Role)).ToArray();
        if (added.Length > 1) throw RetryHookEvidence.Invalid();
        foreach (var item in added)
        {
            if (item.BeforeRevision != before.Revision || after.Revision != before.Revision + 1 ||
                before.Phase != after.Phase) throw RetryHookEvidence.Invalid();
            ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, state.Journal.JournalId);
            var ordinal = state.Total.Steps.ToList().FindIndex(step => step.StepId == item.StepId);
            ExecutionStepAdmission.RequirePredecessorsInside(connection, transaction, state, before, ordinal);
            if (item.Role == "preHook")
            {
                if (before.Phase is not (JournalPhase.Prepared or JournalPhase.Applying) ||
                    WriterIntentCodec.ReadJobs(before.Root, state.Total).Any(job => job.StepId == item.StepId))
                    throw RetryHookEvidence.Invalid();
            }
            else RetryHookEvidence.RequirePost(state.Attempt, state.Total, before, item, state.PreviousInvocationCount(item.StepId));
        }
        RequireSavedInside(connection, transaction, state, after);
    }

    internal static void RequireSavedInside(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument document)
    {
        var dispositions = RetryHookCodec.Read(document.Root, state.Total);
        if (dispositions.IsEmpty) return;
        if (!state.IsExecutionStart) throw RetryHookEvidence.Invalid();
        var current = StartSourceRows.Read(connection, transaction, state.Journal.OperationId);
        var jobs = WriterIntentCodec.ReadJobs(document.Root, state.Total);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        foreach (var item in dispositions)
        {
            if (jobs.Any(job => job.StepId == item.StepId && job.Role == item.Role)) throw RetryHookEvidence.Invalid();
            if (item.Source is { } source)
            {
                if (source.InvocationOrdinal is { } invocation)
                {
                    if (source.OperationId != state.Journal.OperationId || source.JournalId != state.Journal.JournalId ||
                        source.Attempt.RelativePath != state.Journal.PlanPath || source.Attempt.Sha256 != state.Journal.PlanDigest ||
                        invocation >= ExecutionInvocationCodec.Ordinal(document.Root, item.StepId)) throw RetryHookEvidence.Invalid();
                    RequireClosure(source.JournalId, source.Result.Binding.WriterId, source.Result.Binding.JobName); continue;
                }
                var previous = StartSourceRows.Read(connection, transaction, source.OperationId);
                if (previous.RunId != current.RunId || previous.Identity.SubjectId != current.Identity.SubjectId ||
                    previous.AttemptOrdinal >= current.AttemptOrdinal || previous.JournalId != source.JournalId)
                    throw RetryHookEvidence.Invalid();
                var parent = RecoveryJournalRegistry.ReadInside(connection, transaction, source.JournalId);
                if (parent.Phase != JournalPhase.Committed || parent.PlanPath != source.Attempt.RelativePath ||
                    parent.PlanDigest != source.Attempt.Sha256) throw RetryHookEvidence.Invalid();
                var receipt = AttemptReceiptStore.ReadInside(connection, transaction, source.Result.Binding.AttemptId)
                    ?? throw RetryHookEvidence.Invalid();
                if (receipt.JournalId != parent.JournalId || receipt.Facts.RunId != current.RunId ||
                    receipt.Facts.Ordinal != previous.AttemptOrdinal) throw RetryHookEvidence.Invalid();
                RequireClosure(source.JournalId, source.Result.Binding.WriterId, source.Result.Binding.JobName);
            }
            else
                foreach (var job in jobs.Where(job => job.StepId == item.StepId))
                {
                    if (job.ClosedElapsedMilliseconds is null) throw RetryHookEvidence.Invalid();
                    RequireClosure(state.Journal.JournalId, job.WriterId, job.JobName);
                }
        }
        void RequireClosure(Guid journal, Guid writer, string job)
        {
            command.Parameters.Clear(); command.Parameters.AddWithValue("$journal", journal.ToString("D"));
            command.Parameters.AddWithValue("$writer", writer.ToString("D")); command.Parameters.AddWithValue("$job", job);
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.journal_writer_job_closures WHERE journal_id=$journal AND writer_id=$writer AND job_name=$job);";
            if ((long)command.ExecuteScalar()! != 1) throw RetryHookEvidence.Invalid();
        }
    }
}
