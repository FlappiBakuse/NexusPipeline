using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Secrets;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal static class SecretFieldRows
{
    internal static SecretFieldReference ReadField(SqliteConnection connection, SqliteTransaction? transaction, SecretFieldKey key)
    {
        RequireDatabaseKey(connection, transaction, key);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision,secret_id FROM secret_fields WHERE owner_kind=$kind AND owner_id=$owner AND subject_id=$subject AND field=$field;";
        BindKey(command, key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1))) : new(0, null);
    }

    internal static void RequireReference(SqliteConnection connection, SqliteTransaction? transaction, SecretFieldKey key, SecretFieldReference expected)
    {
        if (ReadField(connection, transaction, key) != expected) throw new InvalidOperationException("secret_revision_conflict");
    }

    internal static void RequireDatabaseKey(SqliteConnection connection, SqliteTransaction? transaction, SecretFieldKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.OwnerKind == FieldOwnerKind.Extension) return;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT instance_id FROM schema_info WHERE singleton=1;";
        var expected = key.OwnerKind == FieldOwnerKind.Host ? key.SubjectId : key.OwnerInstanceId;
        if ((string?)command.ExecuteScalar() != expected?.ToString("D"))
            throw new IOException(key.OwnerKind == FieldOwnerKind.Host ? "secret_host_instance_unbound" : "secret_notification_instance_unbound");
    }

    internal static void BindKey(SqliteCommand command, SecretFieldKey key)
    {
        var (kind, owner) = key.OwnerKind switch
        {
            FieldOwnerKind.Extension => ("extension", key.ExtensionId!),
            FieldOwnerKind.Host => ("host", key.SubjectId.ToString("D")),
            FieldOwnerKind.NotificationTask => ("notificationTask", key.OwnerInstanceId!.Value.ToString("D")),
            _ => throw new ArgumentException("secret_field_owner_invalid")
        };
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$subject", key.SubjectId.ToString("D"));
        command.Parameters.AddWithValue("$field", key.Field);
    }
}
