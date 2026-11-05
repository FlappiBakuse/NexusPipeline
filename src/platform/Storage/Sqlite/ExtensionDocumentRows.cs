using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Extensibility.State;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class ExtensionDocumentRows
{
    internal const long MaximumRevision = 9007199254740991;

    internal static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static ExtensionDocument? Read(SqliteConnection connection, SqliteTransaction? transaction,
        string extensionId, string name)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT revision,content,sha256 FROM extension_documents WHERE extension_id=$extension AND name=$name;";
        command.Parameters.AddWithValue("$extension", extensionId); command.Parameters.AddWithValue("$name", name);
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        var revision = row.GetInt64(0); var bytes = (byte[])row[1]; var digest = row.GetString(2);
        if (revision is < 1 or > MaximumRevision || bytes.Length is < 1 or > 1048576 || Digest(bytes) != digest)
            throw new IOException("extension_document_unrecognized");
        using var parsed = StrictJson.Parse(bytes);
        return new(revision, digest, bytes);
    }

    internal static DocumentCommit? ReadCommit(SqliteConnection connection, SqliteTransaction? transaction,
        Guid operationId, string extensionId, string subject)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT o.subject_id,o.operation_kind,o.status,c.extension_id,c.revision,c.sha256 " +
            "FROM operations o LEFT JOIN extension_document_commits c ON c.operation_id=o.operation_id WHERE o.operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetString(0) != subject) return null;
        if (row.GetString(1) != "extension.document.write" || row.GetString(2) != "completed" || row.IsDBNull(3) ||
            row.GetString(3) != extensionId) throw new OperationConflictException();
        return new(operationId, row.GetInt64(4), row.GetString(5), false);
    }

    internal static DocumentCommit Write(SqliteConnection connection, SqliteTransaction transaction,
        OperationIdentity identity, string extensionId, string name, long expectedRevision, byte[] bytes, string digest)
    {
        var accepted = OperationRows.Accept(connection, transaction, identity, DateTimeOffset.UtcNow);
        if (!accepted.IsNew) return ReadCommit(connection, transaction, identity.OperationId, extensionId, identity.SubjectId)
            ?? throw new IOException("extension_document_commit_missing");
        return WriteAccepted(connection, transaction, identity, extensionId, name, expectedRevision, bytes, digest);
    }

    internal static DocumentCommit WriteAccepted(SqliteConnection connection, SqliteTransaction transaction,
        OperationIdentity identity, string extensionId, string name, long expectedRevision, byte[] bytes, string digest)
    {
        if (OperationRows.ReadExisting(connection, transaction, identity)?.Status != "accepted")
            throw new IOException("extension_document_acceptance_missing");
        var before = Read(connection, transaction, extensionId, name);
        if ((before?.Revision ?? 0) != expectedRevision) throw new InvalidOperationException("extension_document_revision_conflict");
        if (expectedRevision == MaximumRevision) throw new InvalidOperationException("extension_document_revision_exhausted");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        if (before is null)
        {
            command.CommandText = "SELECT COUNT(*) FROM extension_documents WHERE extension_id=$extension;";
            command.Parameters.AddWithValue("$extension", extensionId);
            if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) >= 64)
                throw new IOException("extension_document_count_limit");
            command.Parameters.Clear();
        }
        var revision = expectedRevision + 1;
        command.CommandText = "INSERT INTO extension_documents(extension_id,name,revision,content,sha256,operation_id) " +
            "VALUES($extension,$name,$revision,$content,$sha,$operation) ON CONFLICT(extension_id,name) DO UPDATE SET " +
            "revision=excluded.revision,content=excluded.content,sha256=excluded.sha256,operation_id=excluded.operation_id;";
        command.Parameters.AddWithValue("$extension", extensionId); command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$revision", revision); command.Parameters.AddWithValue("$content", bytes);
        command.Parameters.AddWithValue("$sha", digest); command.Parameters.AddWithValue("$operation", identity.OperationId.ToString("D"));
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO extension_document_commits(operation_id,extension_id,name,revision,sha256) " +
            "VALUES($operation,$extension,$name,$revision,$sha);";
        command.Parameters.RemoveAt("$content"); command.ExecuteNonQuery();
        command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$operation;";
        command.ExecuteNonQuery();
        return new(identity.OperationId, revision, digest, true);
    }

    internal static OperationIdentity Identity(Guid operationId, string subject, string extensionId,
        string name, long expectedRevision, string digest) => new(operationId, subject,
            Digest(JsonSerializer.SerializeToUtf8Bytes(new { extensionId, name, expectedRevision, sha256 = digest })), "extension.document.write");
}
