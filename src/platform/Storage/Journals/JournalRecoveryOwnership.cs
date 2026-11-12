using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Journals;

public sealed class JournalRecoveryOwnership(StateDatabase database) : IRecoveryExecutionOwnership
{
    private Guid? _executing;
    private StateDatabase Database => database;
    private readonly JournalWriterStore _writers = new(database);

    public WriterStopObservation Observe(Guid journalId) => _writers.Observe(journalId, _executing);

    public void VerifyStopped(IReadOnlyList<RegisteredJournal> journals)
    {
        foreach (var journal in journals.Where(item => item.Phase is not (JournalPhase.Committed or JournalPhase.Quarantined)))
        {
            var observation = Observe(journal.JournalId);
            if (!observation.IsComplete || observation.State != WriterState.Stopped)
                throw new IOException("startup_journal_writer_stop_unconfirmed");
        }
    }

    public IDisposable Begin(IReadOnlyList<RegisteredJournal> journals)
    {
        if (_executing.HasValue) throw new InvalidOperationException("recovery_execution_already_owned");
        VerifyRecoverable(journals);
        var current = JournalWriterStore.CurrentWriter with { WriterId = Guid.NewGuid() };
        database.Write((connection, transaction) =>
        {
            foreach (var journal in journals.Where(item => item.Phase is not (JournalPhase.Committed or JournalPhase.Quarantined)))
                JournalWriterStore.Insert(connection, transaction, journal.JournalId, current);
            return true;
        });
        _executing = current.WriterId;
        return new Lease(this);
    }

    internal void VerifyRecoverable(IReadOnlyList<RegisteredJournal> journals)
    {
        foreach (var journal in journals.Where(item => item.Phase is not (JournalPhase.Committed or JournalPhase.Quarantined)))
        {
            if (Observe(journal.JournalId) is { IsComplete: true, State: WriterState.Stopped }) continue;
            // A stopped execution owner permits metadata isolation, never an unproven physical restoration.
            if (CanIsolateExecution(journal) && _writers.ObserveCreators(journal.JournalId, _executing)
                    is { IsComplete: true, State: WriterState.Stopped }) continue;
            throw new IOException("startup_journal_writer_stop_unconfirmed");
        }
    }

    private bool CanIsolateExecution(RegisteredJournal journal)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT j.journal_id FROM operation_journals j JOIN operations o ON o.operation_id=j.operation_id " +
            "WHERE j.journal_id=$id AND j.operation_kind='runAttempt' AND o.operation_kind='execution.start' AND o.initial_journal_id=j.journal_id " +
            "UNION SELECT p.journal_id FROM configuration_run_resources r JOIN configuration_run_sessions s USING(session_id) " +
            "JOIN operation_journals c ON c.journal_id=r.journal_id JOIN operation_journals p ON p.journal_id=s.parent_journal_id " +
            "JOIN operations o ON o.operation_id=p.operation_id WHERE r.journal_id=$id AND c.operation_kind='configExchange' " +
            "AND p.operation_kind='runAttempt' AND o.operation_kind='execution.start' AND o.initial_journal_id=p.journal_id;";
        command.Parameters.AddWithValue("$id", journal.JournalId.ToString("D"));
        if (command.ExecuteScalar() is not string parent) return false;
        var instance = new InstanceRoot(new()).OpenExisting(database.RuntimeRoot);
        var state = AttemptWriterState.Read(instance, database, new(), Guid.Parse(parent));
        return state.Total.Steps.All(step => step.Target is null && step.Adapter is null && step.Hooks is not { Count: > 0 }) &&
            ExecutionCaptureCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256).IsEmpty &&
            PreviewDomainCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256).IsEmpty;
    }

    [SupportedOSPlatform("windows")]
    internal ExtensionStopProof CaptureExtensionWriters(RuntimeInstance instance, StateDatabase expected,
        InstanceLease lease, Guid journalId) => ExtensionStopProof.Capture(this, instance, expected, lease, journalId);

    [SupportedOSPlatform("windows")]
    private (Guid Executor, ImmutableArray<JournalWriter> Writers) ObserveExtensionWriters(
        RuntimeInstance instance, StateDatabase expected, InstanceLease lease, Guid journalId)
    {
        lease.VerifyHeld(instance);
        if (!ReferenceEquals(database, expected) || _executing is not { } executor ||
            database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(database.DatabasePath),
                Path.Combine(instance.RuntimeRoot, "state", "nexus.db")))
            throw new IOException("extension_recovery_owner_unbound");
        var before = ReadExtensionWriters(journalId);
        if (before.SingleOrDefault(item => item.WriterId == executor)?.Process != JournalWriterStore.CurrentWriter.Process)
            throw new IOException("extension_recovery_executor_unbound");
        var actual = Observe(journalId);
        if (!actual.IsComplete || actual.State != WriterState.Stopped || !before.SequenceEqual(ReadExtensionWriters(journalId)))
            throw new IOException("extension_original_writer_stop_unconfirmed");
        return (executor, before);
    }

    private ImmutableArray<JournalWriter> ReadExtensionWriters(Guid journalId)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = ReadExtensionWritersInside(connection, transaction, journalId);
        transaction.Commit();
        return result;
    }

    private static ImmutableArray<JournalWriter> ReadExtensionWritersInside(SqliteConnection connection,
        SqliteTransaction transaction, Guid journalId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM journal_writer_jobs WHERE journal_id=$id);";
        command.Parameters.AddWithValue("$id", journalId.ToString("D"));
        if ((long)command.ExecuteScalar()! != 0) throw new IOException("extension_recovery_jobs_unrecognized");
        command.CommandText = "SELECT writer_id,process_id,created_file_time,image_path,image_sha256,user_sid,elevated " +
            "FROM journal_writers WHERE journal_id=$id ORDER BY writer_id LIMIT 1025;";
        using var rows = command.ExecuteReader();
        var result = ImmutableArray.CreateBuilder<JournalWriter>();
        while (rows.Read())
        {
            if (result.Count >= 1024) throw new IOException("extension_recovery_writer_limit");
            result.Add(new(Guid.ParseExact(rows.GetString(0), "D"), new(rows.GetInt32(1),
                DateTime.FromFileTimeUtc(rows.GetInt64(2)), rows.GetString(3), rows.GetString(4), rows.GetString(5), rows.GetBoolean(6))));
        }
        if (result.Count == 0) throw new IOException("extension_recovery_writers_missing");
        return result.ToImmutable();
    }

    [SupportedOSPlatform("windows")]
    internal ExtensionStopProof CaptureFailedActivation(RuntimeInstance instance, StateDatabase expected,
        InstanceLease lease, PreparedExtensionActivation.FailedCallback failure) =>
        ExtensionStopProof.CaptureFailure(this, instance, expected, lease, failure);

    internal sealed class ExtensionStopProof
    {
        private readonly JournalRecoveryOwnership _owner;
        private readonly RuntimeInstance _instance;
        private readonly InstanceLease _lease;
        private readonly Guid _journal, _executor;
        private readonly ImmutableArray<JournalWriter> _writers;
        private readonly PreparedExtensionActivation.FailedCallback? _failure;
        [SupportedOSPlatform("windows")]
        internal ActivationFailureFacts? Failure => _failure?.Facts;

        private ExtensionStopProof(JournalRecoveryOwnership owner, RuntimeInstance instance, InstanceLease lease,
            Guid journal, Guid executor, ImmutableArray<JournalWriter> writers, PreparedExtensionActivation.FailedCallback? failure = null)
        { _owner = owner; _instance = instance; _lease = lease; _journal = journal; _executor = executor; _writers = writers; _failure = failure; }

        [SupportedOSPlatform("windows")]
        internal static ExtensionStopProof Capture(JournalRecoveryOwnership owner, RuntimeInstance instance,
            StateDatabase expected, InstanceLease lease, Guid journalId)
        {
            var actual = owner.ObserveExtensionWriters(instance, expected, lease, journalId);
            return new(owner, instance, lease, journalId, actual.Executor, actual.Writers);
        }

        [SupportedOSPlatform("windows")]
        internal static ExtensionStopProof CaptureFailure(JournalRecoveryOwnership owner, RuntimeInstance instance,
            StateDatabase expected, InstanceLease lease, PreparedExtensionActivation.FailedCallback failure)
        {
            failure.Require(instance, expected, lease, failure.JournalId);
            if (!ReferenceEquals(owner.Database, expected) || expected.InstanceId != instance.InstanceId ||
                expected.RuntimeRoot != instance.RuntimeRoot || !StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetFullPath(expected.DatabasePath), Path.Combine(instance.RuntimeRoot, "state", "nexus.db")))
                throw new IOException("extension_live_failure_owner_unbound");
            var before = owner.ReadExtensionWriters(failure.JournalId);
            var writer = JournalWriterStore.CurrentWriter;
            if (failure.Facts.Writer != writer || owner._writers.RequireCurrentExecutor(failure.JournalId) != writer.WriterId ||
                before.SingleOrDefault(item => item.WriterId == writer.WriterId) != writer)
                throw new IOException("extension_live_failure_writer_unbound");
            var observed = owner._writers.ObserveForCurrentExecutor(failure.JournalId);
            if (!observed.IsComplete || observed.State != WriterState.Stopped ||
                !before.SequenceEqual(owner.ReadExtensionWriters(failure.JournalId)))
                throw new IOException("extension_original_writer_stop_unconfirmed");
            return new(owner, instance, lease, failure.JournalId, writer.WriterId, before, failure);
        }

        [SupportedOSPlatform("windows")]
        internal void RequireInside(SqliteConnection connection, SqliteTransaction transaction, Guid journal)
        {
            _lease.VerifyHeld(_instance);
            _failure?.Require(_instance, _owner.Database, _lease, journal);
            if (journal != _journal || _failure is null && _owner._executing != _executor ||
                !StringComparer.OrdinalIgnoreCase.Equals(connection.DataSource,
                    PhysicalPathPolicy.NativePath(Path.Combine(_instance.RuntimeRoot, "state", "nexus.db"))) ||
                !ReferenceEquals(connection, transaction.Connection) ||
                !_writers.SequenceEqual(ReadExtensionWritersInside(connection, transaction, journal)))
                throw new IOException("extension_recovery_writer_set_changed");
        }
    }

    [SupportedOSPlatform("windows")]
    internal CompletedExtensionProof CaptureCompletedWriters(RuntimeInstance instance, StateDatabase expected,
        InstanceLease lease, Guid journalId, ResourceLeaseManager? resources = null, ResourceOperationLease? root = null,
        string? extensionId = null, ProcessIdentityLease? executor = null) =>
        CompletedExtensionProof.Capture(this, instance, expected, lease, journalId, resources, root, extensionId, executor);

    internal sealed class CompletedExtensionProof
    {
        private readonly RuntimeInstance _instance;
        private readonly InstanceLease _lease;
        private readonly Guid _journal;
        private readonly ImmutableArray<JournalWriter> _writers;
        private readonly ResourceOperationLease? _root;

        private CompletedExtensionProof(RuntimeInstance instance, InstanceLease lease,
            Guid journal, ImmutableArray<JournalWriter> writers, ResourceOperationLease? root)
        { _instance = instance; _lease = lease; _journal = journal; _writers = writers; _root = root; }

        [SupportedOSPlatform("windows")]
        internal static CompletedExtensionProof Capture(JournalRecoveryOwnership owner, RuntimeInstance instance,
            StateDatabase expected, InstanceLease lease, Guid journalId, ResourceLeaseManager? resources,
            ResourceOperationLease? root, string? extensionId, ProcessIdentityLease? executor)
        {
            lease.VerifyHeld(instance);
            if (!ReferenceEquals(owner.Database, expected) || expected.InstanceId != instance.InstanceId ||
                expected.RuntimeRoot != instance.RuntimeRoot || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(expected.DatabasePath),
                    Path.Combine(instance.RuntimeRoot, "state", "nexus.db"))) throw Invalid();
            var registered = new RecoveryJournalRegistry(expected).ReadJournal(journalId);
            if (registered.Kind != JournalKind.ExtensionApply || registered.Phase != JournalPhase.Committed) throw Invalid();
            using var borrow = root is null ? null : root.Borrow(resources ?? throw Invalid(), root.OperationId,
                [new("extension:" + (extensionId ?? throw Invalid()), ResourceClaimMode.Mutation)]);
            var before = owner.ReadExtensionWriters(journalId);
            var observed = root is null ? owner._writers.Observe(journalId) : owner._writers.ObserveForCurrentExecutor(journalId, executor);
            if (!observed.IsComplete || observed.State != WriterState.Stopped ||
                !before.SequenceEqual(owner.ReadExtensionWriters(journalId))) throw Invalid();
            return new(instance, lease, journalId, before, root);
        }

        internal void RequireInside(SqliteConnection connection, SqliteTransaction transaction, Guid journal)
        {
            _lease.VerifyHeld(_instance);
            if (journal != _journal || _root is { IsActive: false } ||
                !StringComparer.OrdinalIgnoreCase.Equals(connection.DataSource,
                    PhysicalPathPolicy.NativePath(Path.Combine(_instance.RuntimeRoot, "state", "nexus.db"))) ||
                !ReferenceEquals(connection, transaction.Connection) ||
                !_writers.SequenceEqual(ReadExtensionWritersInside(connection, transaction, journal))) throw Invalid();
        }
        private static IOException Invalid() => new("extension_completed_writer_stop_unconfirmed");
    }

    [SupportedOSPlatform("windows")]
    internal MaintenanceStopProof CaptureMaintenanceWriters(RuntimeInstance instance, StateDatabase expected,
        InstanceLease lease, Guid journalId) => MaintenanceStopProof.Capture(this, instance, expected, lease, journalId);

    [SupportedOSPlatform("windows")]
    internal MaintenanceStopProof CaptureMaintenanceFailure(RuntimeInstance instance, StateDatabase expected,
        InstanceLease lease, MaintenanceEffectSession.FailedEffect failure) =>
        MaintenanceStopProof.CaptureFailure(this, instance, expected, lease, failure);

    private sealed record MaintenanceWriterJob(Guid WriterId, string Name, int Session);
    private sealed record MaintenanceWriterSet(ImmutableArray<JournalWriter> Writers,
        ImmutableArray<MaintenanceWriterJob> Jobs)
    {
        internal bool Same(MaintenanceWriterSet other) => Writers.SequenceEqual(other.Writers) && Jobs.SequenceEqual(other.Jobs);
    }

    private static MaintenanceWriterSet ReadMaintenanceWriters(SqliteConnection connection, SqliteTransaction transaction, Guid journal)
    {
        var registered = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, journal);
        if (registered.Kind != JournalKind.UpdateApply) throw new IOException("maintenance_recovery_kind_unbound");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$journal", journal.ToString("D"));
        command.CommandText = "SELECT writer_id,process_id,created_file_time,image_path,image_sha256,user_sid,elevated " +
            "FROM journal_writers WHERE journal_id=$journal ORDER BY writer_id LIMIT 1025;";
        var writers = ImmutableArray.CreateBuilder<JournalWriter>();
        using (var rows = command.ExecuteReader())
            while (rows.Read())
            {
                if (writers.Count >= 1024) throw new IOException("maintenance_recovery_writer_limit");
                writers.Add(new(Guid.ParseExact(rows.GetString(0), "D"), new(rows.GetInt32(1),
                    DateTime.FromFileTimeUtc(rows.GetInt64(2)), rows.GetString(3), rows.GetString(4), rows.GetString(5), rows.GetBoolean(6))));
            }
        if (writers.Count == 0) throw new IOException("maintenance_recovery_writers_missing");
        command.CommandText = "SELECT writer_id,job_name,session_id FROM journal_writer_jobs WHERE journal_id=$journal " +
            "ORDER BY writer_id,job_name LIMIT 1025;";
        var jobs = ImmutableArray.CreateBuilder<MaintenanceWriterJob>();
        using (var rows = command.ExecuteReader())
            while (rows.Read())
            {
                if (jobs.Count >= 1024) throw new IOException("maintenance_recovery_job_limit");
                jobs.Add(new(Guid.ParseExact(rows.GetString(0), "D"), rows.GetString(1), rows.GetInt32(2)));
            }
        return new(writers.ToImmutable(), jobs.ToImmutable());
    }

    [SupportedOSPlatform("windows")]
    internal sealed class MaintenanceStopProof
    {
        private readonly JournalRecoveryOwnership _owner;
        private readonly RuntimeInstance _instance;
        private readonly InstanceLease _lease;
        private readonly Guid _journal, _executor;
        private readonly MaintenanceWriterSet _set;
        private readonly MaintenanceEffectSession.FailedEffect? _failure;
        internal HelperFailureFacts? Failure => _failure?.Facts;
        private MaintenanceStopProof(JournalRecoveryOwnership owner, RuntimeInstance instance, InstanceLease lease,
            Guid journal, Guid executor, MaintenanceWriterSet set, MaintenanceEffectSession.FailedEffect? failure = null)
        { _owner = owner; _instance = instance; _lease = lease; _journal = journal; _executor = executor; _set = set; _failure = failure; }

        [SupportedOSPlatform("windows")]
        internal static MaintenanceStopProof Capture(JournalRecoveryOwnership owner, RuntimeInstance instance,
            StateDatabase expected, InstanceLease lease, Guid journal)
        {
            lease.VerifyHeld(instance);
            if (!ReferenceEquals(owner.Database, expected) || owner._executing is not { } executor ||
                expected.InstanceId != instance.InstanceId || expected.RuntimeRoot != instance.RuntimeRoot ||
                !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(expected.DatabasePath),
                    Path.Combine(instance.RuntimeRoot, "state", "nexus.db"))) throw Invalid();
            MaintenanceWriterSet Read()
            {
                using var connection = DatabaseConnections.Open(expected.DatabasePath, readOnly: true);
                using var transaction = connection.BeginTransaction(deferred: true);
                var value = ReadMaintenanceWriters(connection, transaction, journal); transaction.Commit(); return value;
            }
            var before = Read();
            if (before.Writers.SingleOrDefault(item => item.WriterId == executor)?.Process != JournalWriterStore.CurrentWriter.Process)
                throw Invalid();
            var observed = owner.Observe(journal);
            if (!observed.IsComplete || observed.State != WriterState.Stopped || !before.Same(Read())) throw Invalid();
            return new(owner, instance, lease, journal, executor, before);
        }

        internal static MaintenanceStopProof CaptureFailure(JournalRecoveryOwnership owner, RuntimeInstance instance,
            StateDatabase expected, InstanceLease lease, MaintenanceEffectSession.FailedEffect failure)
        {
            failure.Require(instance, expected, lease, failure.JournalId);
            if (!ReferenceEquals(owner.Database, expected) || expected.InstanceId != instance.InstanceId ||
                expected.RuntimeRoot != instance.RuntimeRoot || !StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetFullPath(expected.DatabasePath), Path.Combine(instance.RuntimeRoot, "state", "nexus.db"))) throw Invalid();
            MaintenanceWriterSet Read()
            {
                using var connection = DatabaseConnections.Open(expected.DatabasePath, readOnly: true);
                using var transaction = connection.BeginTransaction(deferred: true);
                var value = ReadMaintenanceWriters(connection, transaction, failure.JournalId); transaction.Commit(); return value;
            }
            var before = Read();
            if (before.Writers.SingleOrDefault(item => item.WriterId == failure.Facts.Writer.WriterId) != failure.Facts.Writer ||
                !before.Jobs.Any(item => item.WriterId == failure.Facts.Writer.WriterId && item.Name == failure.Facts.JobName)) throw Invalid();
            failure.Require(instance, expected, lease, failure.JournalId);
            if (!before.Same(Read())) throw Invalid();
            return new(owner, instance, lease, failure.JournalId, failure.Facts.Writer.WriterId, before, failure);
        }

        internal void RequireInside(SqliteConnection connection, SqliteTransaction transaction, Guid journal)
        {
            _lease.VerifyHeld(_instance);
            _failure?.Require(_instance, _owner.Database, _lease, journal);
            if (journal != _journal || _failure is null && _owner._executing != _executor ||
                !StringComparer.OrdinalIgnoreCase.Equals(connection.DataSource,
                    PhysicalPathPolicy.NativePath(Path.Combine(_instance.RuntimeRoot, "state", "nexus.db"))) ||
                !ReferenceEquals(connection, transaction.Connection) ||
                !_set.Same(ReadMaintenanceWriters(connection, transaction, journal))) throw Invalid();
        }
        private static IOException Invalid() => new("maintenance_recovery_writer_stop_unconfirmed");
    }

    private sealed class Lease(JournalRecoveryOwnership owner) : IDisposable
    {
        public void Dispose() => owner._executing = null;
    }
}
