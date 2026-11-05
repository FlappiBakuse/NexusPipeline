using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal sealed record SavedStartupQueue(StartupQueueBatch Batch, StartupQueueOccurrence Occurrence, string? BlockReason)
{
    internal const string Subject = "host:scheduler";
}
internal sealed record StartupQueueDocument(string Format, int Version, StartupQueueBatch Batch);

internal static class StartupQueueRows
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    internal static string Encode(StartupQueueBatch batch) => Encoding.UTF8.GetString(BoundedJson.Serialize(
        new StartupQueueDocument("nexus.schedule.startup", 1, batch), Options));
    internal static OperationIdentity Identity(StartupQueueBatch batch) => new(batch.Generation, SavedStartupQueue.Subject,
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Encode(batch)))), "schedule.startup");

    internal static StartupQueueBatch? ReadBatch(SqliteConnection connection, SqliteTransaction transaction, Guid generation, Guid instance)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT operation_id,document_json FROM startup_queue_batches WHERE generation=$id;";
        command.Parameters.AddWithValue("$id", generation.ToString("D"));
        string operation, json;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            operation = reader.GetString(0); json = reader.GetString(1);
        }
        using var document = StrictJson.Parse(Encoding.UTF8.GetBytes(json));
        var saved = document.RootElement.Deserialize<StartupQueueDocument>(Options) ?? throw Invalid();
        if (saved.Format != "nexus.schedule.startup" || saved.Version != 1) throw Invalid();
        var batch = saved.Batch;
        if (batch.InstanceId != instance || batch.Generation != generation || batch.CreatedAtUtc.Offset != TimeSpan.Zero ||
            batch.CreatedAtUtc <= DateTimeOffset.UnixEpoch || batch.Occurrences.IsDefault || batch.Occurrences.Length > 4096 ||
            Encode(batch) != json || operation != generation.ToString("D") ||
            batch.Occurrences.Select(item => item.QueueId).Distinct().Count() != batch.Occurrences.Length ||
            !batch.Occurrences.Select(item => item.QueueId).SequenceEqual(batch.Occurrences.Select(item => item.QueueId).Order())) throw Invalid();
        foreach (var item in batch.Occurrences)
            if (item.QueueId == Guid.Empty || item.QueueRevision is < 1 or > 9007199254740991 || item.CatalogRevision is < 1 or > 9007199254740991 ||
                item.SettingsRevision is < 0 or > 9007199254740991 || item != StartupQueuePolicy.CreateOccurrence(
                    instance, generation, item.QueueId, item.QueueRevision, item.CatalogRevision, item.SettingsRevision)) throw Invalid();
        if (OperationRows.ReadExisting(connection, transaction, Identity(batch))?.Status != "completed") throw Invalid();
        command.CommandText = "SELECT occurrence_id,queue_id,run_id,start_operation_id FROM startup_queue_occurrences WHERE generation=$id ORDER BY queue_id;";
        var actual = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) actual.Add(string.Join("/", Enumerable.Range(0, 4).Select(reader.GetString)));
        var expected = batch.Occurrences.OrderBy(item => item.QueueId.ToString("D"), StringComparer.Ordinal)
            .Select(item => $"{item.OccurrenceId:D}/{item.QueueId:D}/{item.RunId:D}/{item.StartOperationId:D}");
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal)) throw Invalid();
        return batch;
    }

    internal static SavedStartupQueue Read(SqliteConnection connection, SqliteTransaction transaction, Guid occurrence, Guid instance,
        StartupQueueBatch? verifiedBatch = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT c.generation,b.reason_code FROM startup_queue_occurrences c LEFT JOIN startup_queue_outcomes b ON b.occurrence_id=c.occurrence_id WHERE c.occurrence_id=$id;";
        command.Parameters.AddWithValue("$id", occurrence.ToString("D"));
        Guid generation; string? reason;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw Invalid();
            generation = ParseId(reader.GetString(0)); reason = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
        if (reason is not null) RequireReason(reason);
        var batch = verifiedBatch ?? ReadBatch(connection, transaction, generation, instance) ?? throw Invalid();
        if (batch.InstanceId != instance || batch.Generation != generation) throw Invalid();
        var item = batch.Occurrences.SingleOrDefault(item => item.OccurrenceId == occurrence) ?? throw Invalid();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM operations WHERE operation_id=$start);";
        command.Parameters.AddWithValue("$start", item.StartOperationId.ToString("D"));
        if ((long)command.ExecuteScalar()! != 0)
        {
            if (reason is not null) throw Invalid();
            var source = StartSourceRows.Read(connection, transaction, item.StartOperationId);
            if (source.RunId != item.RunId || source.RetrySourceOperationId is not null || source.AttemptOrdinal != 1 ||
                source.Identity.SubjectId != SavedStartupQueue.Subject || source.Request != new ExecutionSelectionRequest(
                    new QueueTarget(item.QueueId), item.CatalogRevision, item.SettingsRevision)) throw Invalid();
        }
        return new(batch, item, reason);
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, StartupQueueBatch batch)
    {
        var identity = Identity(batch);
        _ = OperationRows.Accept(connection, transaction, identity, batch.CreatedAtUtc);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO startup_queue_batches VALUES($generation,$operation,$json);";
        command.Parameters.AddWithValue("$generation", batch.Generation.ToString("D"));
        command.Parameters.AddWithValue("$operation", identity.OperationId.ToString("D")); command.Parameters.AddWithValue("$json", Encode(batch));
        command.ExecuteNonQuery();
        foreach (var item in batch.Occurrences)
        {
            command.Parameters.Clear(); command.CommandText = "INSERT INTO startup_queue_occurrences VALUES($id,$generation,$queue,$run,$start);";
            command.Parameters.AddWithValue("$id", item.OccurrenceId.ToString("D")); command.Parameters.AddWithValue("$generation", batch.Generation.ToString("D"));
            command.Parameters.AddWithValue("$queue", item.QueueId.ToString("D")); command.Parameters.AddWithValue("$run", item.RunId.ToString("D"));
            command.Parameters.AddWithValue("$start", item.StartOperationId.ToString("D")); command.ExecuteNonQuery();
        }
        command.Parameters.Clear();
        command.CommandText = "UPDATE operations SET status='completed' WHERE operation_id=$id AND operation_kind='schedule.startup' AND status='accepted';";
        command.Parameters.AddWithValue("$id", identity.OperationId.ToString("D"));
        if (command.ExecuteNonQuery() != 1) throw Invalid();
    }

    internal static void RequireReason(string reason)
    {
        if (reason is not ("startup_owner_restarted" or "schedule_definition_changed" or "schedule_definition_deleted" or
            "schedule_settings_changed" or "schedule_inputs_unavailable" or "schedule_budget_continuity_unknown")) throw Invalid();
    }

    internal static Guid ParseId(string text) => Guid.TryParseExact(text, "D", out var value) && value != Guid.Empty && value.ToString("D") == text
        ? value : throw Invalid();
    internal static IOException Invalid() => new("startup_queue_metadata_unbound");
}
