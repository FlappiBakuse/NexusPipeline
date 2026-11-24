using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace NexusPipeline.Platform.Storage.Scheduling;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class StartupQueueStore(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    ResourceLeaseManager resources, TimeProvider clock)
{
    internal StartupQueueBatch Commit()
    {
        RequireInstance();
        using var root = resources.TryAcquire(lease.StartupGeneration,
            [new("scheduling:state", ResourceClaimMode.Mutation), new("automation:definitions", ResourceClaimMode.Reference),
                new("settings:host", ResourceClaimMode.Reference)]).Lease ?? throw new IOException("schedule_admission_blocked");
        return CommitUnderRoot(root);
    }

    internal StartupQueueBatch CommitUnderRoot(ResourceOperationLease root)
    {
        RequireInstance();
        using var held = root.Borrow(resources, root.OperationId,
            [new("scheduling:state", ResourceClaimMode.Mutation), new("automation:definitions", ResourceClaimMode.Reference),
                new("settings:host", ResourceClaimMode.Reference)]);
        return held.Use(() => CommitOwned(root));
    }

    private StartupQueueBatch CommitOwned(ResourceOperationLease root)
    {
        var committed = lease.PublishExecution(() => database.Write((connection, transaction) =>
        {
            var prior = StartupQueueRows.ReadBatch(connection, transaction, lease.StartupGeneration, instance.InstanceId);
            if (prior is not null) return prior;
            var catalog = AutomationRows.Read(connection, transaction);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT revision FROM system.settings_current WHERE singleton=1;";
            var settings = command.ExecuteScalar() is long revision ? revision : throw StartupQueueRows.Invalid();
            var batch = StartupQueuePolicy.Freeze(instance.InstanceId, lease.StartupGeneration, catalog, settings, clock.GetUtcNow());
            StartupQueueRows.Insert(connection, transaction, batch);
            return StartupQueueRows.ReadBatch(connection, transaction, lease.StartupGeneration, instance.InstanceId) ?? throw StartupQueueRows.Invalid();
        }));
        lease.PublishExecution(() => ScheduleQueuedActivities.RegisterWaiting(instance, database, lease, resources, root));
        return committed;
    }

    internal IReadOnlyList<SavedStartupQueue> ReadWaiting(int limit = 64)
    {
        RequireInstance();
        if (limit is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = DatabaseConnections.BeginRead(connection);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT c.occurrence_id FROM main.startup_queue_occurrences c LEFT JOIN main.operations o ON o.operation_id=c.start_operation_id LEFT JOIN main.startup_queue_outcomes b ON b.occurrence_id=c.occurrence_id WHERE c.generation=$generation AND o.operation_id IS NULL AND b.occurrence_id IS NULL ORDER BY c.queue_id LIMIT $limit;";
        command.Parameters.AddWithValue("$generation", lease.StartupGeneration.ToString("D")); command.Parameters.AddWithValue("$limit", limit);
        var ids = new List<Guid>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(StartupQueueRows.ParseId(reader.GetString(0)));
        if (ids.Count == 0) return [];
        var batch = StartupQueueRows.ReadBatch(connection, transaction, lease.StartupGeneration, instance.InstanceId) ?? throw StartupQueueRows.Invalid();
        return ids.Select(id => StartupQueueRows.Read(connection, transaction, id, instance.InstanceId, batch)).ToArray();
    }

    internal void Block(SavedStartupQueue expected, string reason)
    {
        RequireInstance(); StartupQueueRows.RequireReason(reason);
        if (expected.Batch.InstanceId != instance.InstanceId || expected.Batch.Generation != lease.StartupGeneration) throw StartupQueueRows.Invalid();
        using var origin = PreparedExecutionOrigin.CaptureStartup(instance, database, lease, expected.Occurrence.OccurrenceId, allowBlocked: true);
        using var root = origin.AcquireMetadata(resources);
        lease.PublishExecution(() =>
        {
            var blocked = database.Write((connection, transaction) =>
            {
                var actual = StartupQueueRows.Read(connection, transaction, expected.Occurrence.OccurrenceId, instance.InstanceId);
                if (StartupQueueRows.Encode(actual.Batch) != StartupQueueRows.Encode(expected.Batch) || actual.Occurrence != expected.Occurrence)
                    throw StartupQueueRows.Invalid();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "INSERT INTO main.startup_queue_outcomes(occurrence_id,reason_code) SELECT $id,$reason WHERE NOT EXISTS(SELECT 1 FROM main.operations WHERE operation_id=$start) ON CONFLICT(occurrence_id) DO NOTHING;";
                command.Parameters.AddWithValue("$id", actual.Occurrence.OccurrenceId.ToString("D"));
                command.Parameters.AddWithValue("$reason", reason); command.Parameters.AddWithValue("$start", actual.Occurrence.StartOperationId.ToString("D"));
                command.ExecuteNonQuery();
                return StartupQueueRows.Read(connection, transaction, expected.Occurrence.OccurrenceId, instance.InstanceId).BlockReason is not null;
            });
            if (blocked) { lease.RunBudgets.ReleaseBlockedStartup(database, expected.Occurrence.OccurrenceId); origin.ReleaseBlocked(resources); }
        });
    }

    internal void RetirePriorOwners()
    {
        RequireInstance();
        lease.PublishExecution(() => database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO main.startup_queue_outcomes(occurrence_id,reason_code) SELECT c.occurrence_id,'startup_owner_restarted' FROM main.startup_queue_occurrences c LEFT JOIN main.operations o ON o.operation_id=c.start_operation_id WHERE c.generation<>$generation AND o.operation_id IS NULL ON CONFLICT(occurrence_id) DO NOTHING;";
            command.Parameters.AddWithValue("$generation", lease.StartupGeneration.ToString("D")); command.ExecuteNonQuery(); return true;
        }));
    }

    internal bool HasPending()
    {
        RequireInstance();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT NOT EXISTS(SELECT 1 FROM main.startup_queue_batches WHERE generation=$generation) OR EXISTS(SELECT 1 FROM main.startup_queue_occurrences c LEFT JOIN main.operations o ON o.operation_id=c.start_operation_id LEFT JOIN main.startup_queue_outcomes b ON b.occurrence_id=c.occurrence_id WHERE c.generation=$generation AND o.operation_id IS NULL AND b.occurrence_id IS NULL);";
        command.Parameters.AddWithValue("$generation", lease.StartupGeneration.ToString("D"));
        return (long)command.ExecuteScalar()! != 0;
    }

    private void RequireInstance()
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, Path.Combine(instance.RuntimeRoot, "state", "runtime.db")))
            throw StartupQueueRows.Invalid();
    }
}

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
        command.CommandText = "SELECT operation_id,document_json FROM main.startup_queue_batches WHERE generation=$id;";
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
        command.CommandText = "SELECT occurrence_id,queue_id,run_id,start_operation_id FROM main.startup_queue_occurrences WHERE generation=$id ORDER BY queue_id;";
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
        command.CommandText = "SELECT c.generation,b.reason_code FROM main.startup_queue_occurrences c LEFT JOIN main.startup_queue_outcomes b ON b.occurrence_id=c.occurrence_id WHERE c.occurrence_id=$id;";
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
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.operations WHERE operation_id=$start);";
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
        command.CommandText = "INSERT INTO main.startup_queue_batches VALUES($generation,$operation,$json);";
        command.Parameters.AddWithValue("$generation", batch.Generation.ToString("D"));
        command.Parameters.AddWithValue("$operation", identity.OperationId.ToString("D")); command.Parameters.AddWithValue("$json", Encode(batch));
        command.ExecuteNonQuery();
        foreach (var item in batch.Occurrences)
        {
            command.Parameters.Clear(); command.CommandText = "INSERT INTO main.startup_queue_occurrences VALUES($id,$generation,$queue,$run,$start);";
            command.Parameters.AddWithValue("$id", item.OccurrenceId.ToString("D")); command.Parameters.AddWithValue("$generation", batch.Generation.ToString("D"));
            command.Parameters.AddWithValue("$queue", item.QueueId.ToString("D")); command.Parameters.AddWithValue("$run", item.RunId.ToString("D"));
            command.Parameters.AddWithValue("$start", item.StartOperationId.ToString("D")); command.ExecuteNonQuery();
        }
        command.Parameters.Clear();
        command.CommandText = "UPDATE main.operations SET status='completed' WHERE operation_id=$id AND operation_kind='schedule.startup' AND status='accepted';";
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

internal static class StartupQueueInventory
{
    internal static void Verify(RuntimeInstance instance, StateDatabase database)
    {
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot) throw StartupQueueRows.Invalid();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = DatabaseConnections.BeginRead(connection);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var after = "";
        while (true)
        {
            command.Parameters.Clear(); command.CommandText = "SELECT generation FROM main.startup_queue_batches WHERE generation>$after ORDER BY generation LIMIT 128;";
            command.Parameters.AddWithValue("$after", after); var page = new List<string>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) page.Add(reader.GetString(0));
            if (page.Count == 0) break;
            foreach (var id in page)
            {
                var batch = StartupQueueRows.ReadBatch(connection, transaction, StartupQueueRows.ParseId(id), instance.InstanceId) ?? throw StartupQueueRows.Invalid();
                foreach (var occurrence in batch.Occurrences)
                    _ = StartupQueueRows.Read(connection, transaction, occurrence.OccurrenceId, instance.InstanceId, batch);
            }
            after = page[^1];
        }
        command.Parameters.Clear();
        command.CommandText = "SELECT 1 FROM main.operations o LEFT JOIN main.startup_queue_batches b ON b.operation_id=o.operation_id WHERE o.operation_kind='schedule.startup' AND b.operation_id IS NULL LIMIT 1;";
        if (command.ExecuteScalar() is not null) throw StartupQueueRows.Invalid();
        command.CommandText = "SELECT 1 FROM main.startup_queue_outcomes o LEFT JOIN main.startup_queue_occurrences c ON c.occurrence_id=o.occurrence_id WHERE c.occurrence_id IS NULL OR o.reason_code NOT IN ('startup_owner_restarted','schedule_definition_changed','schedule_definition_deleted','schedule_settings_changed','schedule_inputs_unavailable','schedule_budget_continuity_unknown') LIMIT 1;";
        if (command.ExecuteScalar() is not null) throw StartupQueueRows.Invalid();
        command.CommandText = "SELECT 1 FROM main.startup_queue_occurrences c LEFT JOIN main.startup_queue_batches b ON b.generation=c.generation WHERE b.generation IS NULL LIMIT 1;";
        if (command.ExecuteScalar() is not null) throw StartupQueueRows.Invalid();
    }
}

[SupportedOSPlatform("windows")]
internal sealed class StartupQueueDispatch(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    StartupQueueStore store, ExecutionAdmissionService execution, TimeProvider clock)
{
    private bool _registered;
    private bool _drained;
    internal void Initialize()
    {
        if (!_registered) { _ = store.Commit(); _registered = true; }
    }

    internal void Tick(CancellationToken cancellation, bool canCommit = true)
    {
        cancellation.ThrowIfCancellationRequested();
        if (canCommit) Initialize();
        if (_drained) return;
        var waiting = store.ReadWaiting();
        // A committed generation freezes its occurrences; accepted and blocked members never wait again.
        if (_registered && waiting.Count == 0) _drained = true;
        foreach (var saved in waiting)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                using var origin = PreparedExecutionOrigin.CaptureStartup(instance, database, lease, saved.Occurrence.OccurrenceId, revalidate: true);
                _ = execution.StartScheduled(origin, clock.GetUtcNow());
            }
            catch (ExecutionSelectionException failure) when (failure.Message is "execution_resource_conflict" or "execution_host_unavailable" or "execution_admission_inbox_full" or
                "execution_catalog_revision_conflict" or "execution_settings_revision_conflict") { }
            catch (ExecutionSelectionException failure)
            {
                store.Block(saved, failure.Message == "execution_settings_revision_conflict" ? "schedule_settings_changed" : "schedule_definition_changed");
            }
            catch (ConfigurationException) { store.Block(saved, "schedule_inputs_unavailable"); }
            catch (IOException failure) when (failure.Message == "execution_budget_continuity_unknown")
            { store.Block(saved, "schedule_budget_continuity_unknown"); }
            catch (IOException failure) when (failure.Message is "execution_image_changed" or "execution_directory_changed" or
                "execution_physical_claims_changed" or "execution_extension_provider_unavailable")
            { store.Block(saved, "schedule_inputs_unavailable"); }
        }
    }
}
