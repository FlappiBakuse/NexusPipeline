using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Secrets;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Storage.Sqlite;

[SupportedOSPlatform("windows")]
internal static class SecretDocumentRows
{
    internal static BlobReference? Find(SqliteConnection connection, SqliteTransaction? transaction, Guid operation)
    {
        using var command = Command(connection, transaction,
            "SELECT relative_path,size_bytes,sha256 FROM system.secret_document_publications WHERE operation_id=$0;", operation.ToString("D"));
        using var row = command.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetInt64(1), row.GetString(2)) : null;
    }

    internal static string Status(SqliteConnection connection, SqliteTransaction transaction, BoundSecretDocument source)
    {
        var document = source.Document;
        var existing = OperationRows.ReadExisting(connection, transaction, SecretDocumentFormat.Identity(document));
        if (existing is null || existing.Status is not ("accepted" or "completed") || Find(connection, transaction, document.OperationId) != source.Reference)
            throw SecretDocumentFormat.Invalid();
        using var command = Command(connection, transaction,
            "SELECT extension_id,document_name FROM system.secret_document_publications WHERE operation_id=$0;", document.OperationId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetString(0) != document.ExtensionId || row.GetString(1) != document.Name) throw SecretDocumentFormat.Invalid();
        return existing.Status;
    }

    internal static bool Accept(SqliteConnection connection, SqliteTransaction transaction, BoundSecretDocument source)
    {
        var document = source.Document;
        var identity = SecretDocumentFormat.Identity(document);
        var existing = OperationRows.ReadExisting(connection, transaction, identity);
        if (existing is not null) { _ = Status(connection, transaction, source); return false; }
        using (var pending = Command(connection, transaction, "SELECT 1 FROM system.secret_document_publications p " +
            "JOIN main.operations o ON o.operation_id=p.operation_id WHERE p.extension_id=$0 AND p.document_name=$1 AND o.status='accepted' LIMIT 1;",
            document.ExtensionId, document.Name))
            if (pending.ExecuteScalar() is not null) throw new IOException("secret_document_publication_pending");
        Before(connection, transaction, source);
        if (!OperationRows.Accept(connection, transaction, identity, DateTimeOffset.UtcNow).IsNew) throw SecretDocumentFormat.Invalid();
        Execute(connection, transaction, "INSERT INTO system.secret_document_publications VALUES($0,$1,$2,$3,$4,$5);",
            document.OperationId.ToString("D"), document.ExtensionId, document.Name, source.Reference.RelativePath, source.Reference.SizeBytes, source.Reference.Sha256);
        foreach (var change in document.Secrets)
            Execute(connection, transaction, "INSERT INTO system.secret_document_fields VALUES($0,$1,$2,$3);",
                document.OperationId.ToString("D"), change.OperationId.ToString("D"), change.SubjectId.ToString("D"), change.Field);
        return true;
    }

    internal static void Pending(SqliteConnection connection, SqliteTransaction transaction, BoundSecretDocument source)
    {
        if (Status(connection, transaction, source) != "accepted") throw new IOException("secret_document_publication_not_pending");
        Before(connection, transaction, source);
    }

    internal static void Before(SqliteConnection connection, SqliteTransaction transaction, BoundSecretDocument source)
    {
        var document = source.Document;
        if ((ExtensionDocumentRows.Read(connection, transaction, document.ExtensionId, document.Name)?.Revision ?? 0) != document.ExpectedRevision)
            throw new InvalidOperationException("extension_document_revision_conflict");
        foreach (var change in document.Secrets)
            SecretFieldRows.RequireReference(connection, transaction, new(document.ExtensionId, change.SubjectId, change.Field), change.Before);
    }

    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params object[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue("$" + index, values[index]);
        return command;
    }
    internal static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params object[] values)
    { using var command = Command(connection, transaction, sql, values); command.ExecuteNonQuery(); }
}
