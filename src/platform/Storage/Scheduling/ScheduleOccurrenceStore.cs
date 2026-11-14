using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class ScheduleOccurrenceStore(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    ResourceLeaseManager resources, TimeProvider clock) : IScheduleOccurrenceStore
{
    private const int MaximumWaiting = 4096;
    private readonly object _waitingGate = new();
    private long _waitingRevision = -1;
    private int _waitingLimit;
    private ImmutableArray<ScheduledOccurrence> _waiting = [];
    private IReadOnlyList<ScheduleDiagnostic> _diagnostics = [];
    internal IReadOnlyList<ScheduleDiagnostic> Diagnostics => Volatile.Read(ref _diagnostics);

    public ScheduleCheckpoint ReadCheckpoint()
    {
        RequireInstance();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation);
        using var transaction = connection.BeginTransaction(deferred: true);
        var watermark = ScheduleMinuteRows.Checkpoint(connection, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM automation.automation_catalog_state WHERE singleton=1;";
        var revision = command.ExecuteScalar() is long value ? value : throw ScheduleMinuteCodec.Invalid();
        return new(watermark, revision);
    }

    public ImmutableArray<ScheduledOccurrence> CommitMinute(ScheduleMinuteScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return CommitMinuteCore(scan, retainEmpty: true);
    }

    public ImmutableArray<ScheduledOccurrence> CommitCurrentMinute() => CommitMinuteCore(null, retainEmpty: false);

    internal ScheduleMinuteDocument? CommitProviderMinute(long minute)
    {
        if (clock.GetUtcNow().ToUnixTimeSeconds() / 60 != minute) return null;
        _ = CommitMinuteCore(null, retainEmpty: true);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.System);
        using var transaction = connection.BeginTransaction(deferred: true);
        return ScheduleMinuteRows.ReadMinute(connection, transaction, ScheduleMinuteCodec.OperationId(instance.InstanceId, minute), instance.InstanceId);
    }

    private ImmutableArray<ScheduledOccurrence> CommitMinuteCore(ScheduleMinuteScan? scan, bool retainEmpty)
    {
        RequireInstance();
        var admission = resources.TryAcquire(Guid.NewGuid(),
            [new("scheduling:state", ResourceClaimMode.Mutation), new("automation:definitions", ResourceClaimMode.Reference),
                new("settings:host", ResourceClaimMode.Reference)]);
        using var root = admission.Lease ?? throw new IOException("schedule_admission_blocked");
        var committed = lease.PublishExecution(() =>
        {
            RequireInstance();
            var now = clock.GetUtcNow();
            if (now.Offset != TimeSpan.Zero || now < DateTimeOffset.UnixEpoch) throw new IOException("schedule_clock_invalid");
            var operationId = ScheduleMinuteCodec.OperationId(instance.InstanceId, now.ToUnixTimeSeconds() / 60);
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.System))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                var prior = ScheduleMinuteRows.ReadMinute(connection, transaction, operationId, instance.InstanceId);
                if (prior is not null) return Result(connection, transaction, prior);
            }
            NexusPipeline.Core.Automation.AutomationCatalog catalog;
            ScheduleMinuteWatermark watermark;
            long settingsRevision;
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.System))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                catalog = AutomationRows.Read(connection, transaction);
                watermark = ScheduleMinuteRows.Checkpoint(connection, transaction);
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT revision FROM system.settings_current WHERE singleton=1;";
                settingsRevision = command.ExecuteScalar() is long value ? value : throw ScheduleMinuteCodec.Invalid();
            }
            var diagnostics = new List<ScheduleDiagnostic>();
            var expected = ScheduleMinutePolicy.Scan(instance.InstanceId, catalog, watermark, now, diagnostics.Add);
            Volatile.Write(ref _diagnostics, Array.AsReadOnly(diagnostics.ToArray()));
            if (scan is not null && !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(scan, ScheduleMinuteCodec.Options),
                JsonSerializer.SerializeToElement(expected, ScheduleMinuteCodec.Options))) throw new IOException("schedule_scan_changed");
            if (watermark.HighestUtcMinute >= expected.HighestUtcMinute) return [];
            if (!retainEmpty && expected.Occurrences.IsEmpty) return [];
            var document = new ScheduleMinuteDocument("nexus.schedule.minute", 1, instance.InstanceId,
                ScheduleMinuteCodec.Subject, now, settingsRevision, expected);
            var identity = ScheduleMinuteCodec.Identity(document);
            var catalogBytes = BoundedJson.Serialize(catalog);
            return database.Write((connection, transaction) =>
            {
                var replay = ScheduleMinuteRows.ReadMinute(connection, transaction, operationId, instance.InstanceId);
                if (replay is not null) return Result(connection, transaction, replay);
                var actual = ScheduleMinuteRows.Checkpoint(connection, transaction);
                if (actual != watermark || !BoundedJson.Serialize(AutomationRows.Read(connection, transaction)).AsSpan().SequenceEqual(catalogBytes))
                    throw new IOException("schedule_source_revision_conflict");
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT revision FROM system.settings_current WHERE singleton=1;";
                if (command.ExecuteScalar() is not long current || current != settingsRevision)
                    throw new IOException("schedule_source_revision_conflict");
                command.CommandText = "SELECT COUNT(*) FROM main.scheduled_occurrences c LEFT JOIN main.operations o ON o.operation_id=c.start_operation_id LEFT JOIN main.schedule_occurrence_outcomes b ON b.occurrence_id=c.occurrence_id WHERE o.operation_id IS NULL AND b.occurrence_id IS NULL;";
                var waiting = (long)command.ExecuteScalar()!;
                command.CommandText = "SELECT COUNT(*) FROM main.scheduled_provider_pending;";
                waiting += (long)command.ExecuteScalar()!;
                foreach (var candidate in document.Scan.Occurrences)
                {
                    command.CommandText = "SELECT 1 FROM main.scheduled_occurrences WHERE occurrence_id=$occurrence;";
                    command.Parameters.AddWithValue("$occurrence", candidate.OccurrenceId.ToString("D"));
                    if (command.ExecuteScalar() is null) waiting++;
                    command.Parameters.RemoveAt("$occurrence");
                }
                if (waiting > MaximumWaiting) throw new IOException("schedule_waiting_capacity");
                _ = OperationRows.Accept(connection, transaction, identity, now);
                ScheduleMinuteRows.Insert(connection, transaction, document);
                var saved = ScheduleMinuteRows.ReadMinute(connection, transaction, operationId, instance.InstanceId) ?? throw ScheduleMinuteCodec.Invalid();
                return Result(connection, transaction, saved);
            });
        });
        lease.PublishExecution(() => ScheduleQueuedActivities.RegisterWaiting(instance, database, lease, resources, root));
        return committed;
    }

    public ImmutableArray<ScheduledOccurrence> ReadWaiting(int limit)
    {
        if (limit is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(limit));
        RequireInstance();
        lock (_waitingGate)
        {
            var revision = database.DomainSequence(DatabaseDomain.Runtime);
            if (_waitingRevision == revision && _waitingLimit == limit) return _waiting;
            var waiting = ReadWaitingCore(limit);
            if (revision == database.DomainSequence(DatabaseDomain.Runtime))
            { _waiting = waiting; _waitingRevision = revision; _waitingLimit = limit; }
            return waiting;
        }
    }

    private ImmutableArray<ScheduledOccurrence> ReadWaitingCore(int limit)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT c.occurrence_id FROM main.scheduled_occurrences c LEFT JOIN main.operations o ON o.operation_id=c.start_operation_id LEFT JOIN main.schedule_occurrence_outcomes b ON b.occurrence_id=c.occurrence_id WHERE o.operation_id IS NULL AND b.occurrence_id IS NULL ORDER BY c.trigger_utc_minute,c.occurrence_id LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        var ids = new List<Guid>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(Guid.ParseExact(reader.GetString(0), "D"));
        return ids.Select(id => ScheduleMinuteRows.ReadOccurrence(connection, transaction, id, instance.InstanceId)).ToImmutableArray();
    }

    public ScheduledOccurrence BlockWaiting(ScheduledOccurrence expected, string reasonCode)
    {
        RequireInstance(); ScheduleOccurrenceOutcomes.RequireReason(reasonCode);
        if (expected.InstanceId != instance.InstanceId || expected.SubjectId != ScheduleMinuteCodec.Subject)
            throw ScheduleMinuteCodec.Invalid();
        using var origin = PreparedExecutionOrigin.Capture(instance, database, lease, expected.Frozen.OccurrenceId, allowBlocked: true);
        using var root = origin.AcquireMetadata(resources);
        return lease.PublishExecution(() =>
        {
            var saved = database.Write((connection, transaction) =>
        {
            var actual = ScheduleMinuteRows.ReadOccurrence(connection, transaction, expected.Frozen.OccurrenceId, instance.InstanceId);
            if (actual.Frozen != expected.Frozen || actual.SubjectId != expected.SubjectId ||
                actual.InstanceId != expected.InstanceId) throw new IOException("schedule_occurrence_changed");
            if (actual.Phase is ScheduleOccurrencePhase.Accepted or ScheduleOccurrencePhase.Completed) return actual;
            ScheduleOccurrenceOutcomes.Insert(connection, transaction, actual, reasonCode, clock.GetUtcNow());
            return ScheduleMinuteRows.ReadOccurrence(connection, transaction, actual.Frozen.OccurrenceId, instance.InstanceId);
        });
            if (saved.Phase == ScheduleOccurrencePhase.Blocked)
            { lease.RunBudgets.ReleaseBlockedOccurrence(database, saved); origin.ReleaseBlocked(resources); }
            return saved;
        });
    }

    private void RequireInstance()
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new IOException("schedule_instance_unbound");
    }

    private static ImmutableArray<ScheduledOccurrence> Result(SqliteConnection connection, SqliteTransaction transaction, ScheduleMinuteDocument document) =>
        document.Scan.Occurrences.Select(item => ScheduleMinuteRows.ReadOccurrence(connection, transaction, item.OccurrenceId, document.InstanceId)).ToImmutableArray();
}
