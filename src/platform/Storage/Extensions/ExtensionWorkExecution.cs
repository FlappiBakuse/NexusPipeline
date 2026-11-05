using NexusPipeline.Core.Execution;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed class ExtensionWorkExecution
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ContractReader _contracts;
    private readonly PayloadApplyRoots _roots;
    private readonly BoundWorkPage _work;
    private readonly BlobReference _planReference;
    private readonly Action _authority;
    private readonly JournalRecoveryOwnership.CompletedExtensionProof _stop;
    private readonly ExtensionInstalledMetadata? _installed;
    private WorkEvidenceScope? _evidence;
    internal IReadOnlySet<int>? ProtectedPrevious => _evidence?.Previous;

    private ExtensionWorkExecution(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ContractReader contracts, PayloadApplyRoots roots, BoundWorkPage work, BlobReference planReference, Action authority,
        JournalRecoveryOwnership.CompletedExtensionProof stop)
    { _instance = instance; _database = database; _lease = lease; _contracts = contracts; _roots = roots; _work = work; _planReference = planReference; _authority = authority; _stop = stop;
        _installed = new ExtensionInstalledEvidence(instance, database, contracts, roots).ReadCurrent(work.Origin.Producer.Source.ExtensionId)?.Metadata; }

    [SupportedOSPlatform("windows")]
    internal static ExtensionWorkExecution Owned(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ContractReader contracts, PayloadApplyRoots roots, BoundWorkPage work, ResourceOperationLease root,
        ResourceOperationBorrow borrow, ExtensionActivator.ApplyReservation inventory, ResourceLeaseManager resources, BlobReference planReference) =>
        new(instance, database, lease, contracts, roots, work, planReference, () => borrow.Use(() => inventory.Use(root, (_, _, _) =>
        {
            var observed = new JournalWriterStore(database).ObserveForCurrentExecutor(work.Origin.Proof.ProducerJournalId);
            if (!observed.IsComplete || observed.State != WriterState.Stopped) throw Pending();
            return true;
        })), new JournalRecoveryOwnership(database).CaptureCompletedWriters(instance, database, lease,
            work.Origin.Proof.ProducerJournalId, resources, root, work.Origin.Producer.Source.ExtensionId));

    [SupportedOSPlatform("windows")]
    internal static ExtensionWorkExecution Recovery(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ContractReader contracts, PayloadApplyRoots roots, BoundWorkPage work, JournalRecoveryOwnership ownership, BlobReference planReference) =>
        new(instance, database, lease, contracts, roots, work, planReference, () =>
        {
            var cleanup = ownership.Observe(work.Page.JournalId);
            var producer = new JournalWriterStore(database).Observe(work.Origin.Proof.ProducerJournalId);
            if (!cleanup.IsComplete || cleanup.State != WriterState.Stopped ||
                !producer.IsComplete || producer.State != WriterState.Stopped) throw Pending();
        }, new JournalRecoveryOwnership(database).CaptureCompletedWriters(instance, database, lease,
            work.Origin.Proof.ProducerJournalId));

    internal void Require(FrozenFileCleanup plan) => Require(plan, allowUnaccepted: false);
    internal void RequireBeforeAcceptance(FrozenFileCleanup plan) => Require(plan, allowUnaccepted: true);

    private void Require(FrozenFileCleanup plan, bool allowUnaccepted)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _lease.VerifyHeld(_instance);
        if (_database.InstanceId != _instance.InstanceId || _database.RuntimeRoot != _instance.RuntimeRoot ||
            plan.ExtensionWork?.Reference != _work.Reference || plan.JournalId != _work.Page.JournalId ||
            plan.OperationId != _work.Page.OperationId) throw Pending();
        _authority();
        if (_evidence is not null)
        {
            using var heldConnection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
            using var heldTransaction = heldConnection.BeginTransaction(deferred: true);
            RequireInside(heldConnection, heldTransaction, allowUnaccepted); heldTransaction.Commit();
            return;
        }
        var actual = new ExtensionWorkCodec(_instance, _database, _contracts, _roots).ReadPage(_work.Reference);
        if (actual.Origin.Reference != _work.Origin.Reference) throw Pending();
        _ = new ExtensionWorkHistory(_instance, _database, _contracts, _roots).RequirePage(actual);
        var current = new ExtensionInstalledEvidence(_instance, _database, _contracts, _roots)
            .ReadCurrent(actual.Origin.Producer.Source.ExtensionId);
        if (current?.Metadata != _installed) throw Pending();
        if (current?.Metadata.IsInstalled == true)
        {
            var protectedIds = current.Footprint.Payload.OriginalObjects.Select(item => item.Identity).ToHashSet();
            if (actual.Origin.Proof.Nodes.Any(node => protectedIds.Contains(node.Identity))) throw Pending();
        }
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        RequireInside(connection, transaction, allowUnaccepted); transaction.Commit();
    }

    internal void RequireInside(SqliteConnection connection, SqliteTransaction transaction, bool allowUnaccepted = false)
    {
        _stop.RequireInside(connection, transaction, _work.Origin.Proof.ProducerJournalId);
        _evidence?.RequireInside(connection, transaction);
        var producer = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, _work.Origin.Proof.ProducerJournalId);
        if (producer.Phase != NexusPipeline.Core.Configuration.JournalPhase.Committed ||
            producer.Revision != _work.Origin.Proof.ProducerRevision || producer.PlanPath != _work.Origin.Proof.ProducerPlan.RelativePath ||
            producer.PlanDigest != _work.Origin.Proof.ProducerPlan.Sha256 ||
            ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, _work.Origin.Producer.Source.ExtensionId) != _installed)
            throw Pending();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM operation_journals WHERE journal_id=$journal);";
        command.Parameters.AddWithValue("$journal", _work.Page.JournalId.ToString("D"));
        if ((long)command.ExecuteScalar()! == 0)
        {
            if (!allowUnaccepted) throw Pending();
            return;
        }
        var current = RecoveryJournalRegistry.ReadJournalInside(connection, transaction, _work.Page.JournalId);
        var claims = ExtensionWorkClaims.Read(_work.Origin);
        if (current.Kind != NexusPipeline.Core.Configuration.JournalKind.FileCleanup ||
            current.OperationId != _work.Page.OperationId || current.PlanPath != _planReference.RelativePath ||
            current.PlanDigest != _planReference.Sha256 || !current.ResourceKeys.SequenceEqual(claims.Select(item => item.ResourceKey))) throw Pending();
        command.CommandText = "SELECT resource_key,mode FROM journal_resource_claims WHERE journal_id=$journal ORDER BY resource_key LIMIT 4097;";
        using var rows = command.ExecuteReader(); var index = 0;
        while (rows.Read())
            if (index >= claims.Length || rows.GetString(0) != claims[index].ResourceKey || rows.GetInt32(1) != (int)claims[index++].Mode) throw Pending();
        if (index != claims.Length) throw Pending();
    }

    internal IDisposable? ProtectEvidence(FrozenFileCleanup plan)
    {
        if (_evidence is not null) throw new IOException("extension_work_evidence_scope_already_active");
        var latest = new JournalStore(_instance, _contracts).Read(plan.JournalId);
        if (latest.Revision != 1 || latest.Phase != NexusPipeline.Core.Configuration.JournalPhase.Prepared) return null;
        var held = WorkEvidenceScope.TryAcquire(_instance, _database, _contracts, _roots, plan, _installed);
        if (held is null) return null;
        _evidence = held;
        try { Require(plan); return new EvidenceLease(this, held); }
        catch { ReleaseProtectedEvidence(); throw; }
    }

    internal void ReleaseProtectedEvidence()
    {
        var held = _evidence; _evidence = null; held?.Dispose();
    }

    internal void RequireInventory(FrozenFileCleanup plan, System.Text.Json.Nodes.JsonObject journal, IReadOnlySet<int> previous) =>
        ExtensionWorkInventory.Require(plan.ExtensionWork!.Origin,
            ExtensionWorkInventory.Removed(plan, journal, previous), _evidence?.Tree);

    internal void Removed(ExtensionWorkNode node) => _evidence?.Tree?.Removed(node.Index);

    private sealed class EvidenceLease(ExtensionWorkExecution owner, WorkEvidenceScope held) : IDisposable
    {
        public void Dispose() { if (ReferenceEquals(owner._evidence, held)) owner.ReleaseProtectedEvidence(); }
    }

    internal void RequirePlanReference(BlobReference reference)
    {
        if (reference != _planReference) throw Pending();
    }

    internal void Publish(FrozenFileCleanup plan, Action action)
    {
        Require(plan);
        _lease.PublishExecution(() => { _lease.VerifyHeld(_instance); _authority(); action(); return true; });
    }
    private static IOException Pending() => new("extension_work_cleanup_authority_unconfirmed");
}
