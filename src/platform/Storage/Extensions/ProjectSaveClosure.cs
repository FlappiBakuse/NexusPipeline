using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal static class ProjectSaveClosure
{
    internal static string Digest(Guid binding, long bindingRevision, long selectionRevision, string consumer, string provider,
        JsonElement selection, IReadOnlyList<ProjectInputIntent>? intents) => Convert.ToHexStringLower(SHA256.HashData(BoundedJson.Serialize(new
    {
        format = "nexus.project-save-closure", version = 1, binding, bindingRevision, selectionRevision, consumer, provider, selection,
        protectedInputs = (intents ?? []).OrderBy(intent => intent.Key, StringComparer.Ordinal)
    })));

    internal static bool IsClosed(StateDatabase database, Guid operation, string subject, string digest)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT operation_kind FROM operations WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", operation.ToString("D"));
        if (command.ExecuteScalar() is not "project.selection.closed") return false;
        return OperationRows.ReadExisting(connection, transaction, Identity(operation, subject, digest))?.Status == "completed";
    }

    internal static void CloseAbsent(StateDatabase database, Guid operation, string subject, string digest, Action requireCurrent)
    {
        database.Write((connection, transaction) =>
        {
            requireCurrent();
            var identity = Identity(operation, subject, digest);
            var accepted = OperationRows.Accept(connection, transaction, identity, DateTimeOffset.UtcNow);
            if (!accepted.IsNew && accepted.Status != "completed") throw new OperationConflictException();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$id;";
            command.Parameters.AddWithValue("$id", operation.ToString("D")); command.ExecuteNonQuery();
            return true;
        });
    }

    private static OperationIdentity Identity(Guid operation, string subject, string digest) => new(operation, subject, digest, "project.selection.closed");
}

internal sealed record ProjectSaveOutcome(string Outcome, Core.Configuration.ConfigurationSaveResult? Saved);
