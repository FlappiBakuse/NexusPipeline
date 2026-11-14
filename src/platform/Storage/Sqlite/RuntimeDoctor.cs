using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using System.Runtime.InteropServices;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Records;

namespace NexusPipeline.Platform.Storage.Sqlite;

public sealed class RuntimeDoctor : IRuntimeDoctor
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly IRecoveryIndex _recovery;
    private readonly TimeProvider _clock;

    public RuntimeDoctor(RuntimeInstance instance, StateDatabase database, IRecoveryIndex recovery, TimeProvider? clock = null)
    {
        var actual = new InstanceRoot(new()).OpenExisting(instance.RuntimeRoot);
        if (actual.InstanceId != instance.InstanceId || database.InstanceId != instance.InstanceId ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.RuntimeRoot, actual.RuntimeRoot) ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, Path.Combine(actual.RuntimeRoot, "state", "runtime.db")))
            throw new IOException("diagnostic_instance_unbound");
        _instance = actual; _database = database; _recovery = recovery; _clock = clock ?? TimeProvider.System;
    }

    public RuntimeDoctorReport Read(CancellationToken cancellationToken = default) => ReadCore(false, cancellationToken);
    public RuntimeDoctorReport ReadSupport(CancellationToken cancellationToken = default) => ReadCore(true, cancellationToken);

    private RuntimeDoctorReport ReadCore(bool support, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var recovery = _recovery.Current;
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System);
        using var transaction = connection.BeginTransaction(deferred: true);
        RequireIdentity(connection, transaction);
        var counts = ReadCounts(connection, transaction);
        var pending = counts.PendingOperations; var attempts = counts.Attempts; var runs = counts.CompletedRuns;
        RuntimeDiagnosticDetails? details = null;
        if (support)
        {
            var queues = Count(connection, transaction, "SELECT COUNT(*) FROM automation.queues;");
            var waiting = Count(connection, transaction, "SELECT COUNT(*) FROM main.scheduled_occurrences o WHERE NOT EXISTS(SELECT 1 FROM main.execution_start_sources s WHERE s.operation_id=o.start_operation_id) AND NOT EXISTS(SELECT 1 FROM main.schedule_occurrence_outcomes f WHERE f.occurrence_id=o.occurrence_id);");
            var installed = " FROM extensions.extension_installations i JOIN extensions.extension_installation_revisions r ON r.extension_id=i.extension_id AND r.revision=i.revision WHERE r.is_installed=1";
            var extensionCount = Count(connection, transaction, "SELECT COUNT(*)" + installed + ";");
            var managed = Count(connection, transaction, "SELECT COUNT(*)" + installed + " AND r.package_kind='managed-code';");
            var data = Count(connection, transaction, "SELECT COUNT(*)" + installed + " AND r.package_kind='data-specialized';");
            var tail = ReadSafeTail(connection, transaction);
            details = new(Environment.Version.ToString(), RuntimeInformation.ProcessArchitecture.ToString(),
                !recovery.IsReady ? "unverified" : recovery.Isolations.Count != 0 ? "isolated" :
                recovery.ResourceHolds.Count != 0 ? "pending" : "ready", queues, waiting, extensionCount, managed, data, tail)
            {
                OpenConfigurationCount = Count(connection, transaction, "SELECT COUNT(*) FROM configuration.configuration_edit_sessions WHERE phase IN (0,1);"),
                PendingExtensionCount = Count(connection, transaction, "SELECT COUNT(*) FROM main.operation_journals WHERE operation_kind='extensionApply' AND phase<>'committed';"),
                PendingUpdateCount = Count(connection, transaction, "SELECT COUNT(*) FROM system.maintenance_operations WHERE state IN (0,1,4);"),
                Extensions = ReadExtensions(connection, transaction, installed)
            };
        }
        transaction.Commit();
        cancellationToken.ThrowIfCancellationRequested();
        var admission = !ReferenceEquals(recovery, _recovery.Current) ? DiagnosticAdmissionState.ChangedDuringRead :
            recovery.IsReady && recovery.Isolations.Count == 0 && recovery.ResourceHolds.Count == 0
                ? DiagnosticAdmissionState.Ready : DiagnosticAdmissionState.Blocked;
        // Unknown owner reasons and resource names can contain private input; the report carries only typed state and counts.
        return new("nexus.runtime.diagnostic", 1, Guid.NewGuid(), _instance.InstanceId, _clock.GetUtcNow(), admission,
            recovery.Revision, recovery.Isolations.Count, recovery.ResourceHolds.Count, pending, attempts, runs,
            details is null || admission != DiagnosticAdmissionState.ChangedDuringRead ? details : details with { RecoveryState = "changedDuringRead" });
    }

    public RuntimeRecordCounts ReadCounts()
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Records);
        using var transaction = connection.BeginTransaction(deferred: true);
        RequireIdentity(connection, transaction);
        return ReadCounts(connection, transaction);
    }

    private void RequireIdentity(SqliteConnection connection, SqliteTransaction transaction)
    {
        using (var identity = connection.CreateCommand())
        {
            identity.Transaction = transaction;
            identity.CommandText = "SELECT format,schema_version,instance_id FROM main.schema_info WHERE singleton=1;";
            using var row = identity.ExecuteReader();
            if (!row.Read() || row.GetString(0) != "nexus.state.domain" || row.GetInt64(1) != 2 ||
                row.GetString(2) != _instance.InstanceId.ToString("D")) throw new IOException("diagnostic_state_unbound");
        }
    }

    private static RuntimeRecordCounts ReadCounts(SqliteConnection connection, SqliteTransaction transaction) => new(
        Count(connection, transaction, "SELECT COUNT(*) FROM main.operations WHERE status='accepted';"),
        Count(connection, transaction, "SELECT COUNT(*) FROM records.attempt_receipts;"),
        Count(connection, transaction, "SELECT COUNT(*) FROM records.run_records;"));

    private static IReadOnlyList<DiagnosticExtension> ReadExtensions(SqliteConnection connection,
        SqliteTransaction transaction, string installed)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT r.extension_id,r.package_version,r.package_kind" + installed + " ORDER BY r.extension_id LIMIT 257;";
        using var rows = command.ExecuteReader(); var result = new List<DiagnosticExtension>();
        while (rows.Read())
        {
            var id = rows.GetString(0); var version = rows.GetString(1);
            if (result.Count == 256 || id.Length is < 1 or > 80 || id.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-') ||
                version.Length is < 1 or > 80 || version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '+')))
                throw new IOException("diagnostic_extension_invalid");
            result.Add(new(id, version, rows.GetString(2)));
        }
        return result.AsReadOnly();
    }

    private static IReadOnlyList<DiagnosticRunEvent> ReadSafeTail(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        // Only typed completion facts are safe for support export; worker text and private reason codes are never read.
        command.CommandText = "SELECT ended_at_utc_ticks,engine_outcome,business_outcome,lifecycle_outcome,recovery_outcome FROM records.run_records ORDER BY ended_at_utc_ticks DESC,run_id DESC LIMIT 64;";
        using var rows = command.ExecuteReader(); var result = new List<DiagnosticRunEvent>();
        while (rows.Read())
        {
            var engine = (EngineOutcome)rows.GetInt32(1); var business = (BusinessOutcome)rows.GetInt32(2);
            var lifecycle = (LifecycleOutcome)rows.GetInt32(3); var recovery = (RecoveryOutcome)rows.GetInt32(4);
            if (!Enum.IsDefined(engine) || !Enum.IsDefined(business) || !Enum.IsDefined(lifecycle) || !Enum.IsDefined(recovery))
                throw new IOException("diagnostic_outcome_invalid");
            result.Add(new(new DateTimeOffset(rows.GetInt64(0), TimeSpan.Zero), engine, business, lifecycle, recovery));
        }
        return result.AsReadOnly();
    }

    private static long Count(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        return command.ExecuteScalar() is long count && count >= 0 ? count : throw new IOException("diagnostic_count_invalid");
    }
}

public sealed record RuntimeRecordCounts(long PendingOperations, long Attempts, long CompletedRuns);
