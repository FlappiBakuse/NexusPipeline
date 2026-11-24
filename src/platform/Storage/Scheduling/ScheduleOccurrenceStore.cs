using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusPipeline.Platform.Storage.Scheduling;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class ScheduleOccurrenceStore(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    ResourceLeaseManager resources, TimeProvider clock) : IScheduleOccurrenceStore
{
    private readonly object _waitingGate = new();
    private long _waitingRevision = -1;
    private int _waitingLimit;
    private ImmutableArray<ScheduledOccurrence> _waiting = [];
    private (SQLitePCL.sqlite3 Handle, long DataVersion, AutomationCatalog Catalog)? _catalogSnapshot;
    private IReadOnlyList<ScheduleDiagnostic> _diagnostics = [];
    internal IReadOnlyList<ScheduleDiagnostic> Diagnostics => Volatile.Read(ref _diagnostics);

    public ScheduleCheckpoint ReadCheckpoint()
    {
        RequireInstance();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation);
        using var transaction = DatabaseConnections.BeginRead(connection);
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
        using var transaction = DatabaseConnections.BeginRead(connection);
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
            using (var transaction = DatabaseConnections.BeginRead(connection))
            {
                if (ScheduleMinuteRows.ReadMinuteOccurrences(connection, transaction, operationId, instance.InstanceId) is { } prior)
                    return prior;
            }
            AutomationCatalog catalog;
            ScheduleMinuteWatermark watermark;
            long catalogVersion, settingsRevision;
            using var catalogConnection = DatabaseConnections.Open(database.DatabasePath, readOnly: true,
                domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.System);
            using (var transaction = DatabaseConnections.BeginRead(catalogConnection))
            {
                catalogVersion = ReadCatalogVersion(catalogConnection, transaction);
                catalog = ReadCatalog(catalogConnection, transaction, catalogVersion);
                watermark = ScheduleMinuteRows.Checkpoint(catalogConnection, transaction);
                using var command = catalogConnection.CreateCommand(); command.Transaction = transaction;
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
                if (ScheduleMinuteRows.ReadMinuteOccurrences(connection, transaction, operationId, instance.InstanceId) is { } replay)
                    return replay;
                var actual = ScheduleMinuteRows.Checkpoint(connection, transaction);
                // The write transaction reserves automation before comparing versions on the original reader.
                if (actual != watermark || (ReadCatalogVersion(catalogConnection, null) != catalogVersion &&
                    !BoundedJson.Serialize(AutomationRows.Read(connection, transaction)).AsSpan().SequenceEqual(catalogBytes)))
                    throw new IOException("schedule_source_revision_conflict");
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT revision FROM system.settings_current WHERE singleton=1;";
                if (command.ExecuteScalar() is not long current || current != settingsRevision)
                    throw new IOException("schedule_source_revision_conflict");
                var waiting = ScheduleWaitingCapacity.CountInside(connection, transaction);
                foreach (var candidate in document.Scan.Occurrences)
                {
                    command.CommandText = "SELECT 1 FROM main.scheduled_occurrences WHERE occurrence_id=$occurrence;";
                    command.Parameters.AddWithValue("$occurrence", candidate.OccurrenceId.ToString("D"));
                    if (command.ExecuteScalar() is null) waiting++;
                    command.Parameters.RemoveAt("$occurrence");
                }
                if (waiting > ScheduleWaitingCapacity.Maximum) throw new IOException("schedule_waiting_capacity");
                _ = OperationRows.Accept(connection, transaction, identity, now);
                ScheduleMinuteRows.Insert(connection, transaction, document);
                return ScheduleMinuteRows.ReadMinuteOccurrences(connection, transaction, operationId, instance.InstanceId) ??
                    throw ScheduleMinuteCodec.Invalid();
            });
        });
        lease.PublishExecution(() => ScheduleQueuedActivities.RegisterWaiting(instance, database, lease, resources, root));
        return committed;
    }

    private static long ReadCatalogVersion(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = DatabaseConnections.Command(connection, transaction, "PRAGMA automation.data_version;");
        return command.ExecuteScalar() is long value ? value : throw ScheduleMinuteCodec.Invalid();
    }

    private AutomationCatalog ReadCatalog(SqliteConnection connection, SqliteTransaction transaction, long version)
    {
        var handle = connection.Handle!;
        // The read transaction pins the catalog, and SQLite versions are local to the native handle.
        if (_catalogSnapshot is { } saved && ReferenceEquals(saved.Handle, handle) && saved.DataVersion == version)
            return saved.Catalog;
        var catalog = AutomationRows.Read(connection, transaction);
        _catalogSnapshot = (handle, version, catalog);
        return catalog;
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
        using var connection = DatabaseConnections.Open(database.DatabasePath);
        using var transaction = DatabaseConnections.BeginRead(connection, DatabaseDomain.Runtime);
        var ids = ScheduleWaitingCapacity.ReadPageInside(connection, transaction, limit);
        var waiting = ids.Select(id => ScheduleMinuteRows.ReadOccurrence(connection, transaction, id, instance.InstanceId)).ToImmutableArray();
        transaction.Commit();
        return waiting;
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

}

internal static class ScheduleMinuteRows
{
    private const string OccurrenceColumns = "c.occurrence_id,c.first_operation_id,c.position,c.run_id,c.start_operation_id," +
        "c.schedule_id,c.local_date,c.local_minute,c.trigger_utc_minute,o.operation_id,o.subject_id,o.operation_kind,o.status,o.initial_journal_id," +
        ScheduleOccurrenceOutcomes.Columns;
    private const string OccurrenceOperations = " LEFT JOIN main.operations o ON o.operation_id=c.start_operation_id" +
        " LEFT JOIN main.schedule_occurrence_outcomes b ON b.occurrence_id=c.occurrence_id" +
        " LEFT JOIN main.operations bo ON bo.operation_id=b.operation_id";

    internal static ScheduleMinuteWatermark Checkpoint(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction, "SELECT revision,highest_utc_minute FROM main.scheduling_watermark WHERE singleton=1;");
        ScheduleMinuteWatermark result;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw ScheduleMinuteCodec.Invalid();
            result = new(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
            if (reader.Read() || result.Revision < 0 || result.HighestUtcMinute is < 0) throw ScheduleMinuteCodec.Invalid();
        }
        command.CommandText = "SELECT utc_minute FROM main.schedule_minute_claims WHERE watermark_revision=$revision;";
        command.Parameters.AddWithValue("$revision", result.Revision - 1);
        using (var reader = command.ExecuteReader())
        {
            if (result.Revision == 0 ? reader.Read() || result.HighestUtcMinute is not null :
                !reader.Read() || reader.GetInt64(0) != result.HighestUtcMinute || reader.Read())
                throw ScheduleMinuteCodec.Invalid();
        }
        return result;
    }

    internal static ScheduleMinuteDocument? ReadMinute(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, Guid instanceId)
        => ReadMinute(connection, transaction, operationId, instanceId, null, includeOccurrences: false, out _);

    internal static ImmutableArray<ScheduledOccurrence>? ReadMinuteOccurrences(SqliteConnection connection, SqliteTransaction transaction,
        Guid operationId, Guid instanceId)
        => ReadMinute(connection, transaction, operationId, instanceId, null, includeOccurrences: true, out var occurrences) is null
            ? null : occurrences;

    internal sealed class PendingMinute
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly Dictionary<Guid, ScheduledOccurrence> _occurrences;
        internal long SettingsRevision { get; }

        private PendingMinute(SqliteConnection connection, SqliteTransaction transaction,
            Dictionary<Guid, ScheduledOccurrence> occurrences, long settingsRevision)
        {
            _connection = connection; _transaction = transaction;
            _occurrences = occurrences; SettingsRevision = settingsRevision;
        }

        internal static PendingMinute Read(SqliteConnection connection, SqliteTransaction transaction, Guid operationId,
            Guid instanceId, IReadOnlySet<Guid> pending)
        {
            var occurrences = new Dictionary<Guid, ScheduledOccurrence>(pending.Count);
            var minute = ReadMinute(connection, transaction, operationId, instanceId, occurrence =>
            {
                var id = occurrence.Frozen.OccurrenceId;
                if (pending.Contains(id)) occurrences.Add(id, occurrence);
            }, includeOccurrences: false, out _) ?? throw ScheduleMinuteCodec.Invalid();
            if (occurrences.Count != pending.Count) throw ScheduleMinuteCodec.Invalid();
            return new(connection, transaction, occurrences, minute.SettingsRevision);
        }

        internal ScheduledOccurrence RequireWaiting(StateDatabase database, Guid id)
        {
            if (!ReferenceEquals(_transaction.Connection, _connection) ||
                !DatabaseConnections.IsBound(_connection, database.DatabasePath) ||
                !_occurrences.TryGetValue(id, out var occurrence) || occurrence.InstanceId != database.InstanceId)
                throw new IOException("execution_scheduled_origin_unbound");
            if (occurrence.Phase is ScheduleOccurrencePhase.Accepted or ScheduleOccurrencePhase.Completed)
                throw new IOException("execution_scheduled_already_accepted");
            if (occurrence.Phase != ScheduleOccurrencePhase.WaitingForAdmission)
                throw new IOException("execution_occurrence_blocked");
            return occurrence;
        }
    }

    private static ScheduleMinuteDocument? ReadMinute(SqliteConnection connection, SqliteTransaction transaction, Guid operationId,
        Guid instanceId, Action<ScheduledOccurrence>? observe, bool includeOccurrences, out ImmutableArray<ScheduledOccurrence> occurrences)
    {
        occurrences = [];
        using var command = Command(connection, transaction,
            "SELECT m.document_json,m.occurrence_count,o.subject_id,o.request_digest,o.operation_kind,o.status FROM main.schedule_minute_claims m JOIN main.operations o ON o.operation_id=m.operation_id WHERE m.operation_id=$id;");
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        ScheduleMinuteDocument saved;
        int count;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                using var orphan = Command(connection, transaction, "SELECT 1 FROM main.operations WHERE operation_id=$id;");
                orphan.Parameters.AddWithValue("$id", operationId.ToString("D"));
                if (orphan.ExecuteScalar() is not null) throw ScheduleMinuteCodec.Invalid();
                return null;
            }
            var json = reader.GetString(0); count = reader.GetInt32(1);
            saved = ScheduleMinuteCodec.Decode(Encoding.UTF8.GetBytes(json), instanceId);
            var identity = ScheduleMinuteCodec.Identity(saved);
            if (identity.OperationId != operationId || reader.GetString(2) != identity.SubjectId ||
                reader.GetString(3) != identity.RequestDigest || reader.GetString(4) != identity.Kind ||
                reader.GetString(5) != "completed" || count != saved.Scan.Occurrences.Length) throw ScheduleMinuteCodec.Invalid();
        }
        command.CommandText = "SELECT l.position,l.occurrence_id," + OccurrenceColumns + " FROM main.schedule_minute_occurrences l" +
            " LEFT JOIN main.scheduled_occurrences c ON c.occurrence_id=l.occurrence_id" + OccurrenceOperations +
            " WHERE l.operation_id=$id ORDER BY l.position;";
        var members = new List<(int Position, StoredOccurrence Row)>(count);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var position = members.Count;
                if (position >= count || reader.GetInt32(0) != position || reader.IsDBNull(2) ||
                    reader.GetString(1) != saved.Scan.Occurrences[position].OccurrenceId.ToString("D")) throw ScheduleMinuteCodec.Invalid();
                members.Add((position, StoredOccurrence.Read(reader, 2)));
            }
        }
        if (members.Count != count) throw ScheduleMinuteCodec.Invalid();
        var verified = includeOccurrences ? ImmutableArray.CreateBuilder<ScheduledOccurrence>(count) : null;
        if (verified is not null) verified.Count = count;
        foreach (var group in members.GroupBy(member => member.Row.SourceOperationId))
        {
            // A repeated DST local minute references the first accepted immutable occurrence.
            var source = group.Key == operationId ? saved : ReadOccurrenceSource(connection, transaction, group.Key, instanceId);
            foreach (var (position, row) in group)
            {
                var occurrence = saved.Scan.Occurrences[position];
                var original = row.Bind(source);
                if (original.Frozen.QueueId != occurrence.QueueId || original.Frozen.TimeZoneId != occurrence.TimeZoneId || original.Frozen.LocalDate != occurrence.LocalDate ||
                    original.Frozen.LocalMinute != occurrence.LocalMinute || original.Frozen.RunId != occurrence.RunId ||
                    original.Frozen.StartOperationId != occurrence.StartOperationId) throw ScheduleMinuteCodec.Invalid();
                if (verified is not null) verified[position] = original;
                observe?.Invoke(original);
            }
        }
        if (verified is not null) occurrences = verified.MoveToImmutable();
        return saved;
    }

    internal static ScheduledOccurrence ReadOccurrence(SqliteConnection connection, SqliteTransaction transaction, Guid occurrenceId, Guid instanceId)
    {
        using var command = Command(connection, transaction,
            "SELECT " + OccurrenceColumns + " FROM main.scheduled_occurrences c" + OccurrenceOperations + " WHERE c.occurrence_id=$id;");
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("D"));
        StoredOccurrence stored;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw ScheduleMinuteCodec.Invalid();
            stored = StoredOccurrence.Read(reader, 0);
        }
        return stored.Bind(ReadOccurrenceSource(connection, transaction, stored.SourceOperationId, instanceId));
    }

    private static ScheduleMinuteDocument ReadOccurrenceSource(SqliteConnection connection, SqliteTransaction transaction, Guid source, Guid instanceId)
    {
        using var command = Command(connection, transaction,
            "SELECT m.document_json,o.subject_id,o.request_digest,o.operation_kind,o.status FROM main.schedule_minute_claims m" +
            " JOIN main.operations o ON o.operation_id=m.operation_id WHERE m.operation_id=$source;");
        command.Parameters.AddWithValue("$source", source.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw ScheduleMinuteCodec.Invalid();
        var document = ScheduleMinuteCodec.Decode(Encoding.UTF8.GetBytes(reader.GetString(0)), instanceId);
        var identity = ScheduleMinuteCodec.Identity(document);
        if (identity.OperationId != source || reader.GetString(1) != identity.SubjectId || reader.GetString(2) != identity.RequestDigest ||
            reader.GetString(3) != identity.Kind || reader.GetString(4) != "completed") throw ScheduleMinuteCodec.Invalid();
        return document;
    }

    private readonly record struct StartOperation(string Subject, string Kind, string Status, string? JournalId);

    private readonly record struct StoredOccurrence(Guid Id, Guid SourceOperationId, int Position, string RunId,
        string StartOperationId, string ScheduleId, string LocalDate, int LocalMinute, long TriggerUtcMinute,
        StartOperation? Start, ScheduleOccurrenceOutcomes.StoredOutcome? Outcome)
    {
        internal static StoredOccurrence Read(SqliteDataReader reader, int offset)
        {
            StartOperation? start = reader.IsDBNull(offset + 9) ? null : new(reader.GetString(offset + 10), reader.GetString(offset + 11),
                reader.GetString(offset + 12), reader.IsDBNull(offset + 13) ? null : reader.GetString(offset + 13));
            return new(Guid.ParseExact(reader.GetString(offset), "D"), Guid.ParseExact(reader.GetString(offset + 1), "D"),
                reader.GetInt32(offset + 2), reader.GetString(offset + 3), reader.GetString(offset + 4), reader.GetString(offset + 5),
                reader.GetString(offset + 6), reader.GetInt32(offset + 7), reader.GetInt64(offset + 8), start,
                ScheduleOccurrenceOutcomes.StoredOutcome.Read(reader, offset + 14));
        }

        internal ScheduledOccurrence Bind(ScheduleMinuteDocument document)
        {
            if (Position < 0 || Position >= document.Scan.Occurrences.Length || document.Scan.Occurrences[Position].OccurrenceId != Id)
                throw ScheduleMinuteCodec.Invalid();
            var proposal = document.Scan.Occurrences[Position];
            if (RunId != proposal.RunId.ToString("D") || StartOperationId != proposal.StartOperationId.ToString("D") ||
                ScheduleId != proposal.ScheduleId.ToString("D") || LocalDate != proposal.LocalDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ||
                LocalMinute != proposal.LocalMinute || TriggerUtcMinute != proposal.TriggerUtcMinute) throw ScheduleMinuteCodec.Invalid();
            var reason = ScheduleOccurrenceOutcomes.Bind(Outcome, document.InstanceId, proposal, document.SubjectId);
            if (Start is not { } start) return new(document.InstanceId, proposal, document.SubjectId,
                reason is null ? ScheduleOccurrencePhase.WaitingForAdmission : ScheduleOccurrencePhase.Blocked, null, null, reason);
            if (reason is not null || start.Subject != document.SubjectId || start.Kind != "execution.start" || start.JournalId is null)
                throw ScheduleMinuteCodec.Invalid();
            var journalId = Guid.ParseExact(start.JournalId, "D");
            return new(document.InstanceId, proposal, document.SubjectId, start.Status == "accepted" ?
                ScheduleOccurrencePhase.Accepted : ScheduleOccurrencePhase.Completed, journalId, null, null);
        }
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, ScheduleMinuteDocument document)
    {
        var identity = ScheduleMinuteCodec.Identity(document);
        using var command = Command(connection, transaction,
            "INSERT INTO main.schedule_minute_claims(operation_id,document_json,occurrence_count,watermark_revision,utc_minute) VALUES($id,$json,$count,$revision,$utc);");
        command.Parameters.AddWithValue("$id", identity.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$json", Encoding.UTF8.GetString(ScheduleMinuteCodec.Encode(document)));
        command.Parameters.AddWithValue("$count", document.Scan.Occurrences.Length);
        command.Parameters.AddWithValue("$revision", document.Scan.ExpectedWatermarkRevision);
        command.Parameters.AddWithValue("$utc", document.Scan.HighestUtcMinute); command.ExecuteNonQuery();
        for (var position = 0; position < document.Scan.Occurrences.Length; position++)
        {
            var item = document.Scan.Occurrences[position];
            command.CommandText = "INSERT INTO main.scheduled_occurrences(occurrence_id,run_id,start_operation_id,schedule_id,local_date,local_minute,trigger_utc_minute,first_operation_id,position) VALUES($occurrence,$run,$start,$schedule,$date,$minute,$utc,$id,$position) ON CONFLICT(occurrence_id) DO NOTHING;";
            command.Parameters.AddWithValue("$occurrence", item.OccurrenceId.ToString("D"));
            command.Parameters.AddWithValue("$run", item.RunId.ToString("D"));
            command.Parameters.AddWithValue("$start", item.StartOperationId.ToString("D"));
            command.Parameters.AddWithValue("$schedule", item.ScheduleId.ToString("D"));
            command.Parameters.AddWithValue("$date", item.LocalDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$minute", item.LocalMinute);
            command.Parameters.AddWithValue("$position", position); command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO main.schedule_minute_occurrences(operation_id,position,occurrence_id) VALUES($id,$position,$occurrence);";
            command.ExecuteNonQuery();
            foreach (var name in new[] { "$occurrence", "$run", "$start", "$schedule", "$date", "$minute", "$position" }) command.Parameters.RemoveAt(name);
        }
        command.CommandText = "UPDATE main.scheduling_watermark SET revision=revision+1,highest_utc_minute=$highest WHERE singleton=1 AND revision=$expected AND (highest_utc_minute IS NULL OR highest_utc_minute<$highest);";
        command.Parameters.AddWithValue("$highest", document.Scan.HighestUtcMinute);
        command.Parameters.AddWithValue("$expected", document.Scan.ExpectedWatermarkRevision);
        if (command.ExecuteNonQuery() != 1) throw new IOException("schedule_watermark_revision_conflict");
        command.CommandText = "UPDATE main.operations SET status='completed' WHERE operation_id=$id AND operation_kind='schedule.minute';";
        if (command.ExecuteNonQuery() != 1) throw ScheduleMinuteCodec.Invalid();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    { var result = connection.CreateCommand(); result.Transaction = transaction; result.CommandText = sql; return result; }
}

internal sealed record ScheduleMinuteDocument(string Format, int Version, Guid InstanceId, string SubjectId,
    DateTimeOffset ObservedAtUtc, long SettingsRevision, ScheduleMinuteScan Scan);

internal static class ScheduleMinuteCodec
{
    internal const string Kind = "schedule.minute";
    internal const string Subject = "host:scheduler";
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static Guid OperationId(Guid instanceId, long utcMinute) => new(
        SHA256.HashData(Encoding.UTF8.GetBytes("nexus.schedule.minute/1/" + instanceId.ToString("D") + "/" +
            utcMinute.ToString(System.Globalization.CultureInfo.InvariantCulture))).AsSpan(0, 16), bigEndian: true);
    internal static byte[] Encode(ScheduleMinuteDocument document) => BoundedJson.Serialize(document, Options);
    internal static OperationIdentity Identity(ScheduleMinuteDocument document) => new(
        OperationId(document.InstanceId, document.ObservedAtUtc.ToUnixTimeSeconds() / 60), document.SubjectId,
        Convert.ToHexStringLower(SHA256.HashData(Encode(document))), Kind);

    internal static ScheduleMinuteDocument Decode(byte[] bytes, Guid instanceId)
    {
        using var json = StrictJson.Parse(bytes);
        var value = json.RootElement.Deserialize<ScheduleMinuteDocument>(Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(json.RootElement, JsonSerializer.SerializeToElement(value, Options)) ||
            value.Format != "nexus.schedule.minute" || value.Version != 1 || value.InstanceId != instanceId ||
            value.SubjectId != Subject || value.ObservedAtUtc.Offset != TimeSpan.Zero ||
            value.ObservedAtUtc < DateTimeOffset.UnixEpoch || value.SettingsRevision < 0 || value.Scan is null ||
            value.Scan.ExpectedWatermarkRevision < 0 || value.Scan.CatalogRevision < 0 ||
            value.Scan.HighestUtcMinute != value.ObservedAtUtc.ToUnixTimeSeconds() / 60 ||
            value.Scan.Occurrences.IsDefault || value.Scan.Occurrences.Length > 4096 ||
            value.Scan.Occurrences.Select(item => item.OccurrenceId).Distinct().Count() != value.Scan.Occurrences.Length)
            throw Invalid();
        foreach (var item in value.Scan.Occurrences)
            if (item.InstanceInvalid() || item.CatalogRevision != value.Scan.CatalogRevision ||
                item.TriggerUtcMinute != value.Scan.HighestUtcMinute) throw Invalid();
        return value;
    }

    private static bool InstanceInvalid(this ScheduledOccurrenceProposal item) => item is null ||
        new[] { item.OccurrenceId, item.RunId, item.StartOperationId, item.ScheduleId, item.QueueId }.Any(id => id == Guid.Empty) ||
        item.ScheduleRevision is < 1 or > 9007199254740991 || item.QueueRevision is < 1 or > 9007199254740991 ||
        item.LocalMinute is < 0 or > 1439 || string.IsNullOrWhiteSpace(item.TimeZoneId) || item.TimeZoneId.Length > 128;
    internal static IOException Invalid() => new("schedule_minute_metadata_unbound");
}

internal static class ScheduleOccurrenceOutcomes
{
    private const string Kind = "schedule.occurrence.block";
    internal const string Columns = "b.operation_id,b.reason_code,bo.subject_id,bo.request_digest,bo.operation_kind,bo.status,bo.initial_journal_id";

    internal readonly record struct StoredOutcome(string OperationId, string Reason, string? Subject, string? Digest,
        string? OperationKind, string? Status, bool HasJournal)
    {
        internal static StoredOutcome? Read(SqliteDataReader reader, int offset)
        {
            if (reader.IsDBNull(offset)) return null;
            string? Optional(int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
            return new(reader.GetString(offset), reader.GetString(offset + 1), Optional(offset + 2), Optional(offset + 3),
                Optional(offset + 4), Optional(offset + 5), !reader.IsDBNull(offset + 6));
        }
    }

    internal static void RequireReason(string reason)
    {
        if (reason is not ("schedule_source_unavailable" or "schedule_source_cancelled" or "schedule_budget_continuity_unknown"))
            throw new ArgumentException("schedule_block_reason_invalid");
    }

    private static OperationIdentity Identity(Guid instance, ScheduledOccurrenceProposal frozen, string subject, string reason)
    {
        RequireReason(reason);
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("nexus.schedule.block/1/" + instance.ToString("D") + "/" +
            frozen.OccurrenceId.ToString("D"))).AsSpan(0, 16), bigEndian: true);
        var bytes = BoundedJson.Serialize(new { instanceId = instance, frozen, subject, reason }, ScheduleMinuteCodec.Options);
        return new(id, subject, Convert.ToHexStringLower(SHA256.HashData(bytes)), Kind);
    }

    internal static string? Read(SqliteConnection connection, SqliteTransaction transaction, Guid instance,
        ScheduledOccurrenceProposal frozen, string subject)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT " + Columns + " FROM main.schedule_occurrence_outcomes b " +
            "LEFT JOIN main.operations bo ON bo.operation_id=b.operation_id WHERE b.occurrence_id=$occurrence;";
        command.Parameters.AddWithValue("$occurrence", frozen.OccurrenceId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Bind(StoredOutcome.Read(reader, 0), instance, frozen, subject) : null;
    }

    internal static string? Bind(StoredOutcome? outcome, Guid instance, ScheduledOccurrenceProposal frozen, string subject)
    {
        if (outcome is not { } row) return null;
        var identity = Identity(instance, frozen, subject, row.Reason);
        if (row.OperationId != identity.OperationId.ToString("D") || row.Subject != identity.SubjectId ||
            row.Digest != identity.RequestDigest || row.OperationKind != Kind || row.Status != "completed" || row.HasJournal)
            throw ScheduleMinuteCodec.Invalid();
        return row.Reason;
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, ScheduledOccurrence occurrence,
        string reason, DateTimeOffset now)
    {
        var identity = Identity(occurrence.InstanceId, occurrence.Frozen, occurrence.SubjectId, reason);
        var accepted = OperationRows.Accept(connection, transaction, identity, now);
        if (!accepted.IsNew)
        {
            if (Read(connection, transaction, occurrence.InstanceId, occurrence.Frozen, occurrence.SubjectId) != reason)
                throw ScheduleMinuteCodec.Invalid();
            return;
        }
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO main.schedule_occurrence_outcomes(occurrence_id,operation_id,reason_code) VALUES($occurrence,$operation,$reason);";
        command.Parameters.AddWithValue("$occurrence", occurrence.Frozen.OccurrenceId.ToString("D"));
        command.Parameters.AddWithValue("$operation", identity.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$reason", reason); command.ExecuteNonQuery();
        command.CommandText = "UPDATE main.operations SET status='completed' WHERE operation_id=$operation;"; command.ExecuteNonQuery();
    }

    internal static void VerifyInventory(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM main.operations o LEFT JOIN main.schedule_occurrence_outcomes b ON b.operation_id=o.operation_id WHERE o.operation_kind='schedule.occurrence.block' AND b.operation_id IS NULL LIMIT 1;";
        if (command.ExecuteScalar() is not null) throw ScheduleMinuteCodec.Invalid();
    }
}

internal static class ScheduleMinuteInventory
{
    internal static void Verify(StateDatabase database)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime);
        using var transaction = DatabaseConnections.BeginRead(connection);
        var checkpoint = ScheduleMinuteRows.Checkpoint(connection, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT operation_id,watermark_revision,utc_minute FROM main.schedule_minute_claims ORDER BY watermark_revision;";
        long revision = 0; long? highest = null;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.GetInt64(1) != revision || highest >= reader.GetInt64(2)) throw ScheduleMinuteCodec.Invalid();
                var document = ScheduleMinuteRows.ReadMinute(connection, transaction, Guid.ParseExact(reader.GetString(0), "D"), database.InstanceId)
                    ?? throw ScheduleMinuteCodec.Invalid();
                if (document.Scan.ExpectedWatermarkRevision != revision || document.Scan.HighestUtcMinute != reader.GetInt64(2))
                    throw ScheduleMinuteCodec.Invalid();
                highest = document.Scan.HighestUtcMinute; revision++;
            }
        }
        ScheduleOccurrenceOutcomes.VerifyInventory(connection, transaction);
        if (checkpoint.Revision != revision || checkpoint.HighestUtcMinute != highest) throw ScheduleMinuteCodec.Invalid();
        command.CommandText = "SELECT 1 FROM main.operations o LEFT JOIN main.schedule_minute_claims m ON m.operation_id=o.operation_id WHERE o.operation_kind='schedule.minute' AND m.operation_id IS NULL LIMIT 1;";
        if (command.ExecuteScalar() is not null) throw ScheduleMinuteCodec.Invalid();
        command.CommandText = "SELECT occurrence_id FROM main.scheduled_occurrences ORDER BY occurrence_id;";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                _ = ScheduleMinuteRows.ReadOccurrence(connection, transaction, Guid.ParseExact(reader.GetString(0), "D"), database.InstanceId);
        }
        command.CommandText = "SELECT 1 FROM main.scheduled_occurrences c LEFT JOIN main.schedule_minute_occurrences l ON l.operation_id=c.first_operation_id AND l.position=c.position AND l.occurrence_id=c.occurrence_id WHERE l.occurrence_id IS NULL LIMIT 1;";
        if (command.ExecuteScalar() is not null) throw ScheduleMinuteCodec.Invalid();
    }
}
