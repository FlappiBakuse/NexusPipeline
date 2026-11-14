using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record ProviderTerminalAdmission(long Revision, ProviderTerminalIntent Item);

internal static class ProviderTerminalProjection
{
    internal static IReadOnlyList<ProviderTerminalAdmission> Read(RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, AttemptWriterState state, JournalDocument projected, IReadOnlyList<JournalDocument> tail)
    {
        var prior = ProviderTerminalCodec.ReadHistory(projected.Root, state.Total);
        var results = new List<ProviderTerminalAdmission>();
        foreach (var next in tail)
        {
            var current = ProviderTerminalCodec.ReadHistory(next.Root, state.Total);
            foreach (var added in current.Skip(prior.Length))
            {
                if (next.Phase != JournalPhase.Applying || added.BeforeRevision != next.Revision - 1)
                    throw new IOException("execution_provider_publication_unbound");
                var replay = new ProviderControlReader(instance, database, contracts).Read(added.Binding, added.Artifact);
                if (replay.Terminal != added.Decision) throw new IOException("execution_provider_terminal_unbound");
                results.Add(new(next.Revision, added));
            }
            if (!current.Take(prior.Length).SequenceEqual(prior)) throw new IOException("execution_provider_history_changed");
            prior = current;
        }
        return results.AsReadOnly();
    }

    internal static void RequireInside(SqliteConnection connection, SqliteTransaction transaction, AttemptWriterState state,
        ProviderTerminalAdmission admission)
    {
        if (state.Journal.Phase != JournalPhase.Applying || state.Gate.State != EffectGateState.Entered)
            throw new IOException("execution_provider_admission_closed");
        ExecutionCancellationRows.RequireDispatchOpen(connection, transaction, state.Journal.JournalId);
        var binding = admission.Item.Binding;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.journal_writer_job_roots r JOIN main.journal_writer_jobs j " +
            "ON j.journal_id=r.journal_id AND j.writer_id=r.writer_id AND j.job_name=r.job_name " +
            "WHERE r.journal_id=$journal AND r.writer_id=$writer AND r.job_name=$job AND j.session_id=$session);";
        command.Parameters.AddWithValue("$journal", binding.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$writer", binding.WriterId.ToString("D"));
        command.Parameters.AddWithValue("$job", binding.JobName); command.Parameters.AddWithValue("$session", binding.SessionId);
        if ((long)command.ExecuteScalar()! != 1) throw new IOException("execution_provider_root_unbound");
    }
}
