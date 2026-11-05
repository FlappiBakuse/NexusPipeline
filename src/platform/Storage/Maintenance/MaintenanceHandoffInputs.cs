using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed class MaintenanceHandoffInputs : IDisposable
{
    private readonly List<IDisposable> _held = [];
    private readonly SavedUpdateFeed _head;
    internal RuntimeInstance Instance { get; }
    internal StateDatabase Database { get; }
    internal ContractReader Contracts { get; }
    internal MaintenanceOperationRow Operation { get; }
    internal FrozenPayloadApply Plan { get; }
    internal HostUpdateSource Source { get; }
    internal UpdatePayloadManifest Manifest { get; }

    private MaintenanceHandoffInputs(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        MaintenanceOperationRow row, FrozenPayloadApply plan, HostUpdateSource source, UpdatePayloadManifest manifest,
        SavedUpdateFeed head)
    { Instance = instance; Database = database; Contracts = contracts; Operation = row; Plan = plan; Source = source; Manifest = manifest; _head = head; }

    internal static MaintenanceHandoffInputs Hold(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        PayloadApplyRoots roots, UpdateFeedVerifier verifier, Guid operationId, Guid handoffId, CancellationToken cancellation)
    {
        var row = Read(database, operationId, handoffId);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var plan = new PayloadPlanCodec(instance, contracts, blobs, roots).Read(row.Plan);
        var codec = new UpdateSourceCodec(instance, contracts); var source = codec.ReadMetadata(row.Source);
        MaintenancePendingStore.RequireRow(row, source, plan);
        var head = codec.RequireTrustedContents(source, plan, database, verifier, cancellation);
        var release = codec.RequireFrozenBinding(source, plan);
        using var manifestBytes = blobs.OpenVerified(source.PayloadManifest);
        var original = new byte[checked((int)manifestBytes.Length)]; manifestBytes.ReadExactly(original);
        var manifest = new UpdateManifestCodec(contracts).Read(original, release, requiredProfile: verifier.PayloadProfile);
        var result = new MaintenanceHandoffInputs(instance, database, contracts, row, plan, source, manifest, head);
        try
        {
            foreach (var reference in UpdateSourceCodec.References(source).Append(row.Plan).Append(row.Source)
                .Concat(plan.Before.Contents).Concat(plan.Candidate.Contents).Distinct()) result._held.Add(blobs.OpenVerified(reference));
            result.RequireCurrent(); return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal void RequireCurrent()
    {
        if (_held.Count == 0 || Read(Database, Plan.OperationId, Operation.HandoffId!.Value) != Operation) throw Invalid();
        using var connection = DatabaseConnections.Open(Database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        RequireInside(connection, transaction); transaction.Commit();
    }

    internal void RequireInside(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (_held.Count == 0 || MaintenanceOperationRows.ReadOwned(connection, transaction,
            Operation.Identity.OperationId, Operation.Identity.SubjectId) != Operation) throw Invalid();
        UpdateFeedRows.RequireAccepted(connection, transaction, Source.Feed);
        if (UpdateFeedRows.ReadHead(connection, transaction, Source.Feed.Identity.Channel) != _head) throw Invalid();
        var journal = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, Plan.JournalId);
        if (journal.OperationId != Plan.OperationId || journal.Kind != JournalKind.UpdateApply ||
            journal.PlanPath != Operation.Plan.RelativePath || journal.PlanDigest != Operation.Plan.Sha256 ||
            !journal.ResourceKeys.SequenceEqual(Plan.ResourceKeys) ||
            journal.Phase is not (JournalPhase.Prepared or JournalPhase.Applying or JournalPhase.Applied or
                JournalPhase.Reverting or JournalPhase.Reverted or JournalPhase.Committed)) throw Invalid();
    }

    private static MaintenanceOperationRow Read(StateDatabase database, Guid operationId, Guid handoffId)
    {
        if (operationId == Guid.Empty || handoffId == Guid.Empty) throw Invalid();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var row = MaintenanceOperationRows.ReadAll(connection, transaction).SingleOrDefault(item => item.Identity.OperationId == operationId) ?? throw Invalid();
        if (row.State != HostUpdateState.Starting &&
            !(row.State == HostUpdateState.Completed && row.TerminalCode == "maintenance_original_restored") ||
            row.HandoffId != handoffId || row.InitialJournalId is null) throw Invalid();
        transaction.Commit(); return row;
    }

    public void Dispose()
    {
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _held.Clear();
    }
    private static IOException Invalid() => new("maintenance_handoff_source_unbound");
}
