using Microsoft.Data.Sqlite;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class PreviewDomainEvidence
{
    internal static void RequireSourceInside(SqliteConnection connection, SqliteTransaction transaction,
        PreviewDomainSource source)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT w.process_id,w.created_file_time,w.image_path,w.image_sha256,w.user_sid,w.elevated," +
            "r.process_id,r.created_file_time,r.image_path,r.image_sha256,r.user_sid,r.elevated " +
            "FROM main.journal_writer_jobs b JOIN main.journal_writers w USING(journal_id,writer_id) " +
            "JOIN main.journal_writer_job_roots r USING(journal_id,writer_id,job_name) " +
            "WHERE b.journal_id=$journal AND b.writer_id=$writer AND b.job_name=$job AND b.session_id=$session;";
        command.Parameters.AddWithValue("$journal", source.Primary.ParentJournalId.ToString("D"));
        command.Parameters.AddWithValue("$writer", source.Primary.WriterId.ToString("D"));
        command.Parameters.AddWithValue("$job", source.Primary.JobName);
        command.Parameters.AddWithValue("$session", source.Primary.SessionId);
        using var row = command.ExecuteReader();
        if (!row.Read() || ReadIdentity(row, 0) != source.Creator || ReadIdentity(row, 6) != source.MainRoot || row.Read())
            throw PreviewDomainCodec.Invalid();
    }

    private static ProcessIdentity ReadIdentity(SqliteDataReader row, int offset) => new(row.GetInt32(offset),
        DateTime.FromFileTimeUtc(row.GetInt64(offset + 1)), row.GetString(offset + 2), row.GetString(offset + 3),
        row.GetString(offset + 4), row.GetBoolean(offset + 5));

    [SupportedOSPlatform("windows")]
    internal static void RequireStopped(StateDatabase database, AttemptWriterState state, string? stepId = null)
    {
        PreviewDomainCodec.RequireHistory(state.RequireValidatedRead().History, state.Total, state.Attempt.ExecutionPlan.Sha256);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = connection.BeginTransaction(deferred: true);
        foreach (var item in PreviewDomainCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .Where(item => stepId is null || item.Source.Primary.StepId == stepId))
        {
            RequireSourceInside(connection, transaction, item.Source);
            if (!item.Closed) RequireColdStopped(item.Source);
            else if (!OwnedPreviewJob.ObserveStopped(item.Source.JobName, item.Source.Primary.SessionId)) throw Pending();
        }
        transaction.Commit();
    }

    [SupportedOSPlatform("windows")]
    internal static void RequireColdStopped(PreviewDomainSource source)
    {
        if (WriterStopEvidence.ObserveProcess(source.Creator) != WriterState.Stopped ||
            !OwnedPreviewJob.ObserveStopped(source.JobName, source.Primary.SessionId)) throw Pending();
    }

    private static IOException Pending() => new("execution_preview_domain_stop_unconfirmed");
}

[SupportedOSPlatform("windows")]
internal sealed class PreparedPreviewClosure
{
    private readonly OwnedPreviewJob _owner;
    private readonly PreviewDomainSource _source;
    private PreparedPreviewClosure(OwnedPreviewJob owner, PreviewDomainSource source)
    { _owner = owner; _source = source; }

    internal static PreparedPreviewClosure FromStoppedOwner(OwnedPreviewJob owner, PreviewDomainSource source)
    {
        if (owner.Name != source.JobName || owner.RequireStopped().Flags != source.Flags) throw PreviewDomainCodec.Invalid();
        return new(owner, source);
    }

    internal void Require(PreviewDomainIntent item)
    {
        if (item.Stage != "closed" || item.Source != _source || _source.Creator != JournalWriterStore.CurrentWriter.Process ||
            _source.Primary.WriterId != JournalWriterStore.CurrentWriter.WriterId || _owner.Name != _source.JobName ||
            _owner.RequireStopped().TotalProcesses != item.TotalProcesses) throw PreviewDomainCodec.Invalid();
    }
}
