using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Automation;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal sealed class PreparedExecutionOrigin : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ScheduledOccurrence? _saved;
    private readonly SavedStartupQueue? _startup;
    private readonly long _queueRevision;
    private readonly bool _revalidated;
    private bool _disposed;
    internal Guid OccurrenceId => _saved?.Frozen.OccurrenceId ?? _startup!.Occurrence.OccurrenceId;
    internal Guid RunId => _saved?.Frozen.RunId ?? _startup!.Occurrence.RunId;
    internal Guid OperationId => _saved?.Frozen.StartOperationId ?? _startup!.Occurrence.StartOperationId;
    internal string SubjectId => _saved?.SubjectId ?? SavedStartupQueue.Subject;
    internal ExecutionSelectionRequest Request { get; }

    private PreparedExecutionOrigin(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ScheduledOccurrence saved, long settingsRevision, long? catalogRevision = null, long? queueRevision = null, bool revalidated = false)
    {
        _instance = instance; _database = database; _lease = lease; _saved = saved;
        _queueRevision = queueRevision ?? saved.Frozen.QueueRevision; _revalidated = revalidated;
        Request = new(new QueueTarget(saved.Frozen.QueueId), catalogRevision ?? saved.Frozen.CatalogRevision, settingsRevision);
    }

    private PreparedExecutionOrigin(RuntimeInstance instance, StateDatabase database, InstanceLease lease, SavedStartupQueue saved,
        long? catalogRevision = null, long? settingsRevision = null, long? queueRevision = null, bool revalidated = false)
    {
        _instance = instance; _database = database; _lease = lease; _startup = saved;
        _queueRevision = queueRevision ?? saved.Occurrence.QueueRevision; _revalidated = revalidated;
        Request = new(new QueueTarget(saved.Occurrence.QueueId), catalogRevision ?? saved.Occurrence.CatalogRevision,
            settingsRevision ?? saved.Occurrence.SettingsRevision);
    }

    internal static PreparedExecutionOrigin CaptureStartup(RuntimeInstance instance, StateDatabase database, InstanceLease lease, Guid occurrenceId,
        bool allowBlocked = false, bool revalidate = false)
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, Path.Combine(instance.RuntimeRoot, "state", "nexus.db"))) throw Invalid();
        using var connection = NexusPipeline.Platform.Storage.Sqlite.DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var saved = StartupQueueRows.Read(connection, transaction, occurrenceId, instance.InstanceId);
        if (!allowBlocked && saved.BlockReason is not null || saved.Batch.Generation != lease.StartupGeneration) throw new IOException("execution_occurrence_blocked");
        if (revalidate)
        {
            var catalog = NexusPipeline.Platform.Storage.Automation.AutomationRows.Read(connection, transaction);
            var queue = RequireStartupQueue(catalog, saved.Occurrence.QueueId);
            return new(instance, database, lease, saved, catalog.Revision, SettingsRevision(connection, transaction), queue.Revision, true);
        }
        return new(instance, database, lease, saved);
    }

    internal static PreparedExecutionOrigin Capture(RuntimeInstance instance, StateDatabase database, InstanceLease lease, Guid occurrenceId,
        bool allowBlocked = false, bool revalidate = false)
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            !StringComparer.OrdinalIgnoreCase.Equals(database.DatabasePath, Path.Combine(instance.RuntimeRoot, "state", "nexus.db"))) throw Invalid();
        using var connection = NexusPipeline.Platform.Storage.Sqlite.DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var saved = ScheduleMinuteRows.ReadOccurrence(connection, transaction, occurrenceId, instance.InstanceId);
        if (!allowBlocked && saved.Phase == ScheduleOccurrencePhase.Blocked) throw new IOException("execution_occurrence_blocked");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT first_operation_id FROM scheduled_occurrences WHERE occurrence_id=$id;";
        command.Parameters.AddWithValue("$id", occurrenceId.ToString("D"));
        var source = command.ExecuteScalar() as string ?? throw Invalid();
        var document = ScheduleMinuteRows.ReadMinute(connection, transaction, Guid.ParseExact(source, "D"), instance.InstanceId) ?? throw Invalid();
        if (revalidate)
        {
            var catalog = NexusPipeline.Platform.Storage.Automation.AutomationRows.Read(connection, transaction);
            var queue = ScheduledSourcePolicy.RequireCurrent(saved.Frozen, catalog);
            return new(instance, database, lease, saved, SettingsRevision(connection, transaction), catalog.Revision, queue.Revision, true);
        }
        return new(instance, database, lease, saved, document.SettingsRevision);
    }

    internal AcceptedExecutionRun RegisterUnderRoot(ResourceLeaseManager resources, ResourceOperationLease root)
    {
        RequireRequest(Request);
        using var connection = NexusPipeline.Platform.Storage.Sqlite.DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        if (_startup is not null)
        {
            var actual = StartupQueueRows.Read(connection, transaction, OccurrenceId, _instance.InstanceId);
            if (actual.BlockReason is not null || actual.Occurrence != _startup.Occurrence ||
                StartupQueueRows.Encode(actual.Batch) != StartupQueueRows.Encode(_startup.Batch)) throw Invalid();
        }
        else
        {
            var actual = ScheduleMinuteRows.ReadOccurrence(connection, transaction, OccurrenceId, _instance.InstanceId);
            if (actual.Frozen != _saved!.Frozen) throw Invalid();
            if (actual.Phase is ScheduleOccurrencePhase.Accepted or ScheduleOccurrencePhase.Completed)
                throw new IOException("execution_scheduled_already_accepted");
            if (actual.Phase != ScheduleOccurrencePhase.WaitingForAdmission) throw Invalid();
        }
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM operations WHERE operation_id=$id);";
        command.Parameters.AddWithValue("$id", OperationId.ToString("D"));
        if ((long)command.ExecuteScalar()! != 0) throw new IOException("execution_scheduled_already_accepted");
        if (_revalidated) _ = RequireCurrentSource(connection, transaction);
        var existing = resources.FindQueuedRun(RunId, OperationId, SubjectId, (QueueTarget)Request.Target);
        if (existing is null) return resources.RegisterQueuedRun(root, RunId, ExecutionStartIdentity.Create(OperationId, SubjectId, Request), Request);
        if (_revalidated) resources.RefreshQueuedRun(root, existing, ExecutionStartIdentity.Create(OperationId, SubjectId, Request), Request);
        return existing;
    }

    internal AcceptedExecutionRun QueuedForAdmission(ResourceLeaseManager resources)
    {
        RequireRequest(Request);
        var existing = resources.FindQueuedRun(RunId, OperationId, SubjectId, (QueueTarget)Request.Target);
        if (existing is not null && !_revalidated) return existing;
        var admission = existing is null ? resources.TryAcquire(Guid.NewGuid(), [new("scheduling:state", ResourceClaimMode.Mutation)])
            : resources.TryContinueRun(existing, OperationId, [new("scheduling:state", ResourceClaimMode.Mutation)]);
        using var metadata = admission.Lease
            ?? throw new ExecutionSelectionException("execution_host_unavailable");
        try { return _lease.PublishExecution(() => RegisterUnderRoot(resources, metadata)); }
        catch (IOException error) when (error.Message == "execution_scheduled_already_accepted") { throw; }
        catch (ExecutionSelectionException) { throw; }
        catch
        {
            resources.ReportUncertainOwnership(metadata, "schedule_activity_reconciliation_required");
            throw;
        }
    }

    internal ResourceOperationLease AcquireMetadata(ResourceLeaseManager resources)
    {
        RequireRequest(Request);
        var existing = resources.FindQueuedRun(RunId, OperationId, SubjectId, (QueueTarget)Request.Target);
        var admission = existing is null ? resources.TryAcquire(Guid.NewGuid(), [new("scheduling:state", ResourceClaimMode.Mutation)]) :
            resources.TryContinueRun(existing, OperationId, [new("scheduling:state", ResourceClaimMode.Mutation)]);
        return admission.Lease ?? throw new IOException("schedule_admission_blocked");
    }

    internal void ReleaseBlocked(ResourceLeaseManager resources)
    {
        RequireRequest(Request);
        using var connection = NexusPipeline.Platform.Storage.Sqlite.DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var blocked = _startup is not null ? StartupQueueRows.Read(connection, transaction, OccurrenceId, _instance.InstanceId).BlockReason is not null :
            ScheduleMinuteRows.ReadOccurrence(connection, transaction, OccurrenceId, _instance.InstanceId).Phase == ScheduleOccurrencePhase.Blocked;
        if (!blocked) throw Invalid();
        resources.FindQueuedRun(RunId, OperationId, SubjectId, (QueueTarget)Request.Target)?.Dispose();
    }

    internal void RequireOccurrence(ScheduledOccurrence value)
    {
        if (_saved is null || value.InstanceId != _instance.InstanceId || value.Frozen != _saved.Frozen || value.SubjectId != _saved.SubjectId) throw Invalid();
    }

    internal void RequireRequest(ExecutionSelectionRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); _lease.VerifyHeld(_instance);
        if (ExecutionSelectionPolicy.FreezeRequest(request) != Request) throw Invalid();
    }

    internal void RequireReceipt(AttemptAcceptanceResult receipt, ContractReader contracts)
    {
        var plan = new AttemptPlanCodec(_instance, contracts, new(_instance, contracts)).Read(receipt.Plan);
        RequirePlan(plan.ReadWire());
    }

    internal void RequirePlan(RunAttemptPlan plan)
    {
        if (plan.InstanceId != _instance.InstanceId.ToString("D") || plan.RunId != RunId.ToString("D") ||
            plan.OperationId != OperationId.ToString("D") || plan.AttemptOrdinal != 1) throw Invalid();
    }

    internal void RequireCurrentInside(SqliteConnection connection, SqliteTransaction transaction,
        OperationIdentity operation, ExecutionSelectionRequest request, RunAttemptPlan attempt, ExecutionPlan total)
    {
        RequireRequest(request); RequirePlan(attempt);
        if (!StringComparer.OrdinalIgnoreCase.Equals(connection.DataSource, PhysicalPathPolicy.NativePath(_database.DatabasePath))) throw Invalid();
        Guid queueId; long queueRevision;
        if (_startup is not null)
        {
            var current = StartupQueueRows.Read(connection, transaction, OccurrenceId, _instance.InstanceId);
            if (current.BlockReason is not null || current.Occurrence != _startup.Occurrence ||
                StartupQueueRows.Encode(current.Batch) != StartupQueueRows.Encode(_startup.Batch) || current.Batch.Generation != _lease.StartupGeneration) throw Invalid();
            queueId = current.Occurrence.QueueId; queueRevision = current.Occurrence.QueueRevision;
        }
        else
        {
            var current = ScheduleMinuteRows.ReadOccurrence(connection, transaction, OccurrenceId, _instance.InstanceId);
            if (current != _saved || current.Phase == ScheduleOccurrencePhase.Blocked) throw Invalid();
            queueId = current.Frozen.QueueId; queueRevision = current.Frozen.QueueRevision;
        }
        if (_revalidated) queueRevision = RequireCurrentSource(connection, transaction).Revision;
        if (queueRevision != _queueRevision || operation != ExecutionStartIdentity.Create(OperationId, SubjectId, Request) ||
            total.RunId != RunId.ToString("D") || total.QueueId != queueId.ToString("D") || total.SettingsRevision != Request.ExpectedSettingsRevision ||
            !total.SourceRevisions.Any(item => item.EntityKind == "queue" && item.EntityId == total.QueueId && item.Revision == queueRevision)) throw Invalid();
    }

    private AutomationQueue RequireCurrentSource(SqliteConnection connection, SqliteTransaction transaction)
    {
        var catalog = NexusPipeline.Platform.Storage.Automation.AutomationRows.Read(connection, transaction);
        var queue = _saved is not null ? ScheduledSourcePolicy.RequireCurrent(_saved.Frozen, catalog) : RequireStartupQueue(catalog, _startup!.Occurrence.QueueId);
        if (catalog.Revision != Request.ExpectedCatalogRevision || queue.Revision != _queueRevision)
            throw new ExecutionSelectionException("execution_catalog_revision_conflict");
        if (SettingsRevision(connection, transaction) != Request.ExpectedSettingsRevision)
            throw new ExecutionSelectionException("execution_settings_revision_conflict");
        return queue;
    }

    private static AutomationQueue RequireStartupQueue(AutomationCatalog catalog, Guid id) =>
        catalog.Queues.SingleOrDefault(queue => queue.Id == id && queue.RunOnStartup && !queue.ScriptIds.IsEmpty)
            ?? throw new ExecutionSelectionException("execution_schedule_cancelled");

    private static long SettingsRevision(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM settings_current WHERE singleton=1;";
        return command.ExecuteScalar() is long revision ? revision : throw Invalid();
    }

    internal static void RequireExplicitOrigin(SqliteConnection connection, SqliteTransaction transaction, Guid operationId,
        PreparedExecutionOrigin? origin)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT occurrence_id,0 FROM scheduled_occurrences WHERE start_operation_id=$id UNION ALL SELECT occurrence_id,1 FROM startup_queue_occurrences WHERE start_operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { if (origin is not null) throw Invalid(); return; }
        if (origin is null || reader.GetString(0) != origin.OccurrenceId.ToString("D") ||
            reader.GetInt32(1) != (origin._startup is null ? 0 : 1) || reader.Read()) throw Invalid();
    }

    public void Dispose() => _disposed = true;
    private static IOException Invalid() => new("execution_scheduled_origin_unbound");
}
