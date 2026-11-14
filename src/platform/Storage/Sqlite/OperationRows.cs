using System.Globalization;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class OperationRows
{
    internal static OperationSnapshot? ReadOwned(SqliteConnection connection, SqliteTransaction transaction,
        Guid operationId, string subject)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT subject_id,operation_kind,status,initial_journal_id FROM main.operations WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetString(0) != subject) return null;
        var kind = row.GetString(1); var status = row.GetString(2);
        Guid? journalId = null;
        if (!row.IsDBNull(3))
        {
            if (!Guid.TryParseExact(row.GetString(3), "D", out var parsed) || parsed == Guid.Empty)
                throw new IOException("operation_query_registration_invalid");
            journalId = parsed;
        }
        if (kind.Length is < 1 or > 120 || status is not ("accepted" or "completed"))
            throw new IOException("operation_query_registration_invalid");
        return new(operationId, kind, status, journalId);
    }

    internal static OperationAcceptance Accept(SqliteConnection connection, SqliteTransaction transaction, OperationIdentity identity, DateTimeOffset acceptedAt)
    {
        var found = ReadExisting(connection, transaction, identity);
        if (found is not null) return found;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO main.operations(operation_id,subject_id,request_digest,operation_kind,status,created_at) VALUES($id,$subject,$digest,$kind,'accepted',$created);";
        command.Parameters.AddWithValue("$id", identity.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$subject", identity.SubjectId);
        command.Parameters.AddWithValue("$digest", identity.RequestDigest);
        command.Parameters.AddWithValue("$kind", identity.Kind);
        command.Parameters.AddWithValue("$created", acceptedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        return new(identity.OperationId, true, "accepted");
    }

    internal static OperationAcceptance? ReadExisting(SqliteConnection connection, SqliteTransaction transaction, OperationIdentity identity)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT subject_id, request_digest, operation_kind, status FROM main.operations WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", identity.OperationId.ToString("D"));
        using (var existing = command.ExecuteReader())
        {
            if (existing.Read())
            {
                if (existing.GetString(0) != identity.SubjectId || existing.GetString(1) != identity.RequestDigest || existing.GetString(2) != identity.Kind)
                    throw new OperationConflictException();
                return new(identity.OperationId, false, existing.GetString(3));
            }
        }
        return null;
    }
}
