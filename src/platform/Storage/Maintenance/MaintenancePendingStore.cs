using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed record PendingHostUpdate(Guid OperationId, string Subject, HostUpdateTiming Timing);

internal sealed class MaintenancePendingStore
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly ContractReader _contracts;
    private readonly PayloadApplyRoots _roots;
    private readonly UpdateFeedVerifier? _verifier;

    internal MaintenancePendingStore(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceLeaseManager resources, ContractReader contracts, PayloadApplyRoots roots, UpdateFeedVerifier? verifier)
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || !StringComparer.OrdinalIgnoreCase.Equals(database.RuntimeRoot, instance.RuntimeRoot)) throw Invalid();
        _instance = instance; _database = database; _lease = lease; _resources = resources;
        _contracts = contracts; _roots = roots; _verifier = verifier;
    }

    [SupportedOSPlatform("windows")]
    internal HostUpdateOperation Accept(PreparedHostUpdate prepared, DateTimeOffset acceptedAt)
    {
        _lease.VerifyHeld(_instance);
        return prepared.UseAcceptance(_instance, _database, (identity, plan, reference, source) => _lease.PublishExecution(() =>
        {
            var codec = new UpdateSourceCodec(_instance, _contracts);
            codec.RequireFrozenBinding(source, plan);
            if (UpdateRequestIdentity.FromSource(source, identity.SubjectId) != identity || plan.MaintenanceSource is null) throw Invalid();
            var row = _database.Write((connection, transaction) =>
            {
                var prior = OperationRows.ReadExisting(connection, transaction, identity);
                if (prior is not null) return MaintenanceOperationRows.ReadOwned(connection, transaction, identity.OperationId, identity.SubjectId) ?? throw Invalid();
                codec.RequireAcceptedInside(connection, transaction, source);
                OperationRows.Accept(connection, transaction, identity, acceptedAt);
                MaintenanceOperationRows.InsertPending(connection, transaction, identity, reference, plan.MaintenanceSource);
                return MaintenanceOperationRows.ReadOwned(connection, transaction, identity.OperationId, identity.SubjectId) ?? throw Invalid();
            });
            return Snapshot(row);
        }));
    }

    internal IReadOnlyList<PendingHostUpdate> Pending()
    {
        _lease.VerifyHeld(_instance);
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var ids = new List<(Guid Id, string Subject)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT m.operation_id,o.subject_id FROM maintenance_operations m JOIN operations o ON o.operation_id=m.operation_id WHERE m.state=0 ORDER BY o.created_at,m.operation_id LIMIT 64;";
            using var rows = command.ExecuteReader();
            while (rows.Read()) ids.Add((Guid.Parse(rows.GetString(0)), rows.GetString(1)));
        }
        var result = ids.Select(id =>
        {
            var row = MaintenanceOperationRows.ReadOwned(connection, transaction, id.Id, id.Subject) ?? throw Invalid();
            var snapshot = Snapshot(row);
            if (snapshot.State != HostUpdateState.Pending) throw Invalid();
            return new PendingHostUpdate(id.Id, id.Subject, snapshot.Timing);
        }).ToArray();
        transaction.Commit(); return result;
    }

    internal HostUpdateOperation? Read(Guid operationId, string subject)
    {
        RequireIdentity(operationId, subject); _lease.VerifyHeld(_instance);
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var row = MaintenanceOperationRows.ReadOwned(connection, transaction, operationId, subject);
        transaction.Commit(); return row is null ? null : Snapshot(row);
    }

    internal HostUpdateOperation? Cancel(Guid operationId, string subject)
    {
        RequireIdentity(operationId, subject); _lease.VerifyHeld(_instance);
        using var root = _resources.TryAcquire(operationId, [new("maintenance:metadata", ResourceClaimMode.Mutation)]).Lease ?? throw Invalid();
        return _lease.PublishExecution(() =>
        {
            var row = _database.Write((connection, transaction) =>
            {
                var current = MaintenanceOperationRows.ReadOwned(connection, transaction, operationId, subject);
                if (current is null || current.State == HostUpdateState.Cancelled) return current;
                _ = Snapshot(current);
                MaintenanceOperationRows.CancelPending(connection, transaction, current);
                return MaintenanceOperationRows.ReadOwned(connection, transaction, operationId, subject) ?? throw Invalid();
            });
            return row is null ? null : Snapshot(row);
        });
    }

    [SupportedOSPlatform("windows")]
    internal PreparedUpdateHandoff BeginHandoff(Guid operationId, string subject, HostCompletionDrain actualDrain,
        CancellationToken cancellation, IPayloadPersistenceObserver? observer = null)
    {
        RequireIdentity(operationId, subject); _lease.VerifyHeld(_instance);
        if (actualDrain.OperationId != operationId) throw Invalid();
        var root = _resources.TryCompletePayload(actualDrain).Lease ?? throw Invalid();
        TreeReadLease? before = null; PreparedPayloadPlan? frozen = null;
        try
        {
            return _lease.PublishExecution(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                MaintenanceOperationRow row;
                using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true))
                using (var transaction = connection.BeginTransaction(deferred: true))
                { row = MaintenanceOperationRows.ReadOwned(connection, transaction, operationId, subject) ?? throw Invalid(); transaction.Commit(); }
                if (row.State != HostUpdateState.Pending) throw Invalid();
                var codec = new UpdateSourceCodec(_instance, _contracts);
                var source = codec.ReadMetadata(row.Source);
                var blobs = new ImmutableBlobStore(_instance, _contracts);
                var planCodec = new PayloadPlanCodec(_instance, _contracts, blobs, _roots);
                var plan = planCodec.Read(row.Plan);
                RequireRow(row, source, plan);
                var head = codec.RequireTrustedContents(source, plan, _database,
                    _verifier ?? throw new IOException("maintenance_release_trust_unavailable"), cancellation);
                before = TreeReadLease.AcquireApplication(plan.TargetPath, plan.ParentIdentity, plan.Before);
                frozen = planCodec.Freeze(plan);
                if (frozen.Reference != row.Plan) throw Invalid();
                var handoff = Guid.NewGuid();
                var registered = new RegisteredJournal(plan.JournalId, plan.OperationId, plan.Kind, NexusPipeline.Core.Configuration.JournalPhase.Prepared,
                    1, row.Plan.RelativePath, row.Plan.Sha256, plan.ResourceKeys);
                _database.Write((connection, transaction) =>
                {
                    var current = MaintenanceOperationRows.ReadOwned(connection, transaction, operationId, subject) ?? throw Invalid();
                    if (current != row) throw Invalid();
                    codec.RequireAcceptedInside(connection, transaction, source);
                    if (UpdateFeedRows.ReadHead(connection, transaction, head.Identity.Channel) != head) throw Invalid();
                    MaintenanceOperationRows.MarkStarting(connection, transaction, current, handoff);
                    RecoveryJournalRegistry.RegisterInside(connection, transaction, registered, PayloadFileIntents.Create(plan, row.Plan.Sha256),
                        JournalWriterStore.CurrentWriter);
                    return true;
                });
                observer?.Reached(PayloadPersistencePoint.AcceptanceCommitted, registered.JournalId, 1, registered.Phase);
                new PayloadApplyAcceptance(_instance, _contracts, _database, _roots, observer).MaterializeInitial(registered);
                return new PreparedUpdateHandoff(actualDrain, root, before!, frozen, source, handoff);
            });
        }
        catch
        {
            try { frozen?.Dispose(); } finally { try { before?.Dispose(); } finally { root.Dispose(); } }
            throw;
        }
    }

    internal HostUpdateOperation Snapshot(MaintenanceOperationRow row)
    {
        var source = new UpdateSourceCodec(_instance, _contracts).ReadMetadata(row.Source);
        if (UpdateRequestIdentity.FromSource(source, row.Identity.SubjectId) != row.Identity ||
            row.InitialJournalId is { } initial && initial != source.JournalId) throw Invalid();
        HostUpdateOutcome? outcome = row.State != HostUpdateState.Completed ? null : row.TerminalCode switch
        {
            "candidate_started" => HostUpdateOutcome.Updated,
            "maintenance_original_restored" => HostUpdateOutcome.OriginalRestored,
            "payload_not_executed" => HostUpdateOutcome.NotExecuted,
            _ => throw Invalid()
        };
        return new(row.Identity.OperationId, source.HostVersion, source.Timing, row.State, row.InitialJournalId, outcome);
    }

    internal static void RequireRow(MaintenanceOperationRow row, HostUpdateSource source, FrozenPayloadApply plan)
    {
        if (row.Plan.RelativePath != $"blobs/plans/{row.Plan.Sha256}.json" || plan.MaintenanceSource != row.Source ||
            source.InstanceId != plan.InstanceId || source.JournalId != plan.JournalId || source.OperationId != plan.OperationId ||
            UpdateRequestIdentity.FromSource(source, row.Identity.SubjectId) != row.Identity ||
            row.InitialJournalId is { } initial && initial != plan.JournalId) throw Invalid();
    }
    private static void RequireIdentity(Guid operationId, string subject)
    {
        if (operationId == Guid.Empty || subject is not { Length: >= 1 and <= 256 } || subject.Any(char.IsControl)) throw Invalid();
    }
    private static IOException Invalid() => new("maintenance_pending_operation_unbound");
}
