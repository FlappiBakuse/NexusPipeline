using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionBindingSuccesses
{
    internal static IReadOnlyDictionary<Guid, int> Today(StateDatabase database, DateTimeOffset now)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = connection.BeginTransaction(deferred: true);
        var (start, end) = Day(now);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT binding_id,count(*) FROM main.execution_binding_successes WHERE started_at_utc_ticks>=$start AND started_at_utc_ticks<$end AND started_at_utc_ticks<=$now GROUP BY binding_id;";
        command.Parameters.AddWithValue("$start", start.UtcTicks); command.Parameters.AddWithValue("$end", end.UtcTicks); command.Parameters.AddWithValue("$now", now.UtcTicks);
        using var rows = command.ExecuteReader(); var result = new Dictionary<Guid, int>();
        while (rows.Read()) result.Add(Guid.ParseExact(rows.GetString(0), "D"), rows.GetInt32(1));
        return result;
    }

    internal static (DateTimeOffset Start, DateTimeOffset End) Day(DateTimeOffset now)
    {
        var date = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).Date;
        return (new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date, TimeZoneInfo.Local)),
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.AddDays(1), TimeZoneInfo.Local)));
    }

    internal static void Project(SqliteConnection connection, SqliteTransaction transaction,
        AttemptWriterState state, JournalDocument document, IReadOnlyList<JournalDocument> history)
    {
        foreach (var item in StepResultCodec.Coverage(document.Root, state.Total)
            .Where(item => item.Kind == StepCompletionKind.Collected && item.Result is not null))
        {
            var fact = ExecutionBindingRoles.Combine(document.Root, state.Total, item, state.Attempt.ExecutionPlan.Sha256);
            if (RetryDecision.ClassifyCollectedPrimary(fact) != AttemptOutcome.Succeeded) continue;
            var step = state.Total.Steps.Single(step => step.StepId == fact.StepId);
            if (step.Purpose == "initialization") continue;
            using var exists = connection.CreateCommand(); exists.Transaction = transaction;
            exists.CommandText = "SELECT EXISTS(SELECT 1 FROM main.execution_binding_successes WHERE run_id=$run AND binding_id=$binding);";
            exists.Parameters.AddWithValue("$run", state.Attempt.RunId); exists.Parameters.AddWithValue("$binding", (object?)step.BindingId ?? DBNull.Value);
            if ((long)exists.ExecuteScalar()! == 1) continue;
            if (step.BindingId is null || !AttemptTerminalEvidence.IsRestoredInside(connection, transaction,
                state with { Latest = document }, stepId: step.StepId)) continue;
            var started = history.First(revision => revision.Revision <= document.Revision &&
                WriterIntentCodec.ReadJobs(revision.Root, state.Total).Any(job => job.StepId == step.StepId));
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO main.execution_binding_successes(run_id,binding_id,journal_id,started_at_utc_ticks) " +
                "VALUES($run,$binding,$journal,$started) ON CONFLICT(run_id,binding_id) DO NOTHING;";
            command.Parameters.AddWithValue("$run", state.Attempt.RunId); command.Parameters.AddWithValue("$binding", step.BindingId);
            command.Parameters.AddWithValue("$journal", state.Journal.JournalId.ToString("D"));
            command.Parameters.AddWithValue("$started", DateTimeOffset.Parse(started.Root.GetProperty("updatedAt").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture).UtcTicks); command.ExecuteNonQuery();
        }
    }
}
