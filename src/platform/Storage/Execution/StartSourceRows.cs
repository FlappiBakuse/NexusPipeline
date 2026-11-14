using System.Text;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record FrozenStartSource(OperationIdentity Identity, Guid RunId, Guid JournalId,
    ExecutionSelectionRequest Request, Guid? RetrySourceOperationId, int AttemptOrdinal);

internal static class StartSourceRows
{
    internal static FrozenStartSource Read(SqliteConnection connection, SqliteTransaction transaction, Guid operationId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT s.subject_id,s.run_id,s.journal_id,s.request_json,s.retry_source_operation_id,o.subject_id,o.request_digest,o.operation_kind,o.initial_journal_id,s.attempt_ordinal FROM main.execution_start_sources s JOIN main.operations o ON o.operation_id=s.operation_id WHERE s.operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw Invalid();
        var decoded = StartRequestCodec.Decode(Encoding.UTF8.GetBytes(reader.GetString(3)));
        var request = decoded.Request; var identity = decoded.Identity;
        if (identity.OperationId != operationId || identity.SubjectId != reader.GetString(0)) throw Invalid();
        if (reader.GetString(5) != identity.SubjectId || reader.GetString(6) != identity.RequestDigest || reader.GetString(7) != identity.Kind ||
            reader.IsDBNull(8) || reader.GetString(8) != reader.GetString(2)) throw Invalid();
        var run = Guid.ParseExact(reader.GetString(1), "D"); var journal = Guid.ParseExact(reader.GetString(2), "D");
        var original = reader.IsDBNull(4) ? (Guid?)null : Guid.ParseExact(reader.GetString(4), "D");
        if (run != decoded.Document.RunId || journal != decoded.Document.JournalId || original != decoded.Document.RetrySourceOperationId ||
            run == Guid.Empty || journal == Guid.Empty || original == Guid.Empty || original == operationId) throw Invalid();
        var ordinal = reader.GetInt32(9);
        if (ordinal is < 1 or > 99 || (original is null) != (ordinal == 1)) throw Invalid();
        return new(identity, run, journal, request, original, ordinal);
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, OperationIdentity identity,
        ExecutionSelectionRequest request, RunAttemptPlan attempt, Guid? retrySource = null)
    {
        if (identity != ExecutionStartIdentity.Create(identity.OperationId, identity.SubjectId, request) ||
            attempt.OperationId != identity.OperationId.ToString("D")) throw Invalid();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO main.execution_start_sources(operation_id,subject_id,run_id,journal_id,request_json,retry_source_operation_id,attempt_ordinal) VALUES($operation,$subject,$run,$journal,$request,$source,$ordinal);";
        command.Parameters.AddWithValue("$operation", identity.OperationId.ToString("D")); command.Parameters.AddWithValue("$subject", identity.SubjectId);
        command.Parameters.AddWithValue("$ordinal", attempt.AttemptOrdinal);
        command.Parameters.AddWithValue("$run", attempt.RunId); command.Parameters.AddWithValue("$journal", attempt.JournalId);
        var bytes = StartRequestCodec.Encode(identity, request, Guid.Parse(attempt.RunId), Guid.Parse(attempt.JournalId), retrySource);
        if (attempt.StartRequest is not { } reference || reference.SizeBytes != bytes.LongLength ||
            reference.Sha256 != Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) ||
            reference.RelativePath != $"blobs/plans/{reference.Sha256}.json") throw Invalid();
        command.Parameters.AddWithValue("$request", Encoding.UTF8.GetString(bytes));
        command.Parameters.AddWithValue("$source", retrySource is { } source ? source.ToString("D") : DBNull.Value); command.ExecuteNonQuery();
        _ = Read(connection, transaction, identity.OperationId);
    }

    internal static void VerifyInventory(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT o.operation_id FROM main.operations o LEFT JOIN main.execution_start_sources s ON s.operation_id=o.operation_id WHERE o.operation_kind='execution.start' AND s.operation_id IS NULL LIMIT 1;";
        if (command.ExecuteScalar() is not null) throw Invalid();
        command.CommandText = "SELECT operation_id FROM main.execution_start_sources ORDER BY operation_id;";
        var operations = new List<Guid>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) operations.Add(Guid.ParseExact(reader.GetString(0), "D"));
        foreach (var id in operations)
        {
            var source = Read(connection, transaction, id);
            var parent = NexusPipeline.Platform.Storage.Sqlite.RecoveryJournalRegistry.ReadInside(connection, transaction, source.JournalId);
            using var inputs = AttemptPreparedBinding.ReadPlanInputs(instance, contracts, parent);
            var attempt = inputs.Plan;
            RequireAttempt(source, attempt);
            var total = inputs.Total;
            ProjectInitializationPlan.RequireRequest(total, source.Request);
            if (attempt.RunId != source.RunId.ToString("D") || attempt.OperationId != id.ToString("D") ||
                total.SettingsRevision != source.Request.ExpectedSettingsRevision ||
                total.QueueId != (source.Request.Target is QueueTarget queue ? queue.QueueId.ToString("D") : null)) throw Invalid();
            ExecutionConfigurationCursors.Read(instance, contracts, attempt, total).RequireSaved(instance, database, contracts, attempt, total);
            if (source.RetrySourceOperationId is { } prior)
            {
                var original = Read(connection, transaction, prior);
                if (original.RunId != source.RunId || original.Identity.SubjectId != source.Identity.SubjectId ||
                    original.Request != source.Request || original.AttemptOrdinal + 1 != source.AttemptOrdinal) throw Invalid();
            }
        }
    }

    internal static FrozenStartSource Original(SqliteConnection connection, SqliteTransaction transaction,
        FrozenStartSource source)
    {
        var seen = new HashSet<Guid>();
        var current = source;
        for (var count = 0; count < 99; count++)
        {
            if (!seen.Add(current.Identity.OperationId) || current.RunId != source.RunId ||
                current.Identity.SubjectId != source.Identity.SubjectId || current.Request != source.Request) throw Invalid();
            if (current.RetrySourceOperationId is not { } prior) return current;
            var previous = Read(connection, transaction, prior);
            if (previous.AttemptOrdinal + 1 != current.AttemptOrdinal) throw Invalid();
            current = previous;
        }
        throw Invalid();
    }

    internal static void RequireAttempt(FrozenStartSource source, RunAttemptPlan attempt)
    {
        var encoded = StartRequestCodec.Encode(source.Identity, source.Request, source.RunId, source.JournalId, source.RetrySourceOperationId);
        if (attempt.AttemptOrdinal != source.AttemptOrdinal || attempt.StartRequest is not { } reference || reference.SizeBytes != encoded.LongLength ||
            reference.Sha256 != Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(encoded)) ||
            reference.RelativePath != $"blobs/plans/{reference.Sha256}.json") throw Invalid();
    }

    private static IOException Invalid() => new("execution_start_source_unbound");
}
