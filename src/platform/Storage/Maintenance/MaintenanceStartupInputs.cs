using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class MaintenanceStartupInputs : IDisposable
{
    private readonly List<IDisposable> _held = [];
    private readonly IReadOnlyList<PhysicalTreeObject> _installedObjects;
    private readonly SavedUpdateFeed _acceptedHead;
    internal FrozenPayloadApply Plan { get; }
    internal HostUpdateSource Source { get; }
    internal UpdatePayloadManifest Manifest { get; }
    internal PhysicalObjectIdentity InstalledRoot { get; }
    internal MaintenanceOperationRow Operation { get; }
    internal RestoredStartupEvidence? Restored { get; private set; }

    private MaintenanceStartupInputs(FrozenPayloadApply plan, HostUpdateSource source, UpdatePayloadManifest manifest,
        PhysicalObjectIdentity installedRoot, MaintenanceOperationRow operation, IReadOnlyList<PhysicalTreeObject> installedObjects,
        SavedUpdateFeed acceptedHead)
    {
        Plan = plan; Source = source; Manifest = manifest; InstalledRoot = installedRoot; Operation = operation;
        _installedObjects = installedObjects; _acceptedHead = acceptedHead;
    }

    internal static MaintenanceStartupInputs Hold(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        PayloadApplyRoots roots, UpdateFeedVerifier verifier, MaintenanceStartupBinding binding, CancellationToken cancellation)
    {
        MaintenanceStartupFrames.Require(binding);
        if (binding.InstanceId != instance.InstanceId) throw Invalid();
        var row = ReadOperation(database, binding);
        var blobs = new ImmutableBlobStore(instance, contracts);
        var plan = new PayloadPlanCodec(instance, contracts, blobs, roots).Read(row.Plan);
        var codec = new UpdateSourceCodec(instance, contracts);
        var source = codec.ReadMetadata(row.Source);
        MaintenancePendingStore.RequireRow(row, source, plan);
        var head = codec.RequireTrustedContents(source, plan, database, verifier, cancellation);
        var release = codec.RequireFrozenBinding(source, plan);
        var manifest = new UpdateManifestCodec(contracts).Read(Read(blobs, source.PayloadManifest), release, requiredProfile: verifier.PayloadProfile);
        var restored = row.State == HostUpdateState.Completed;
        var installed = restored ? plan.Before : plan.Candidate;
        var actual = ApplicationTree.Capture(new(SnapshotBudget.Default), plan.TargetPath, cancellation);
        if (actual.Shape != installed.Snapshot.Shape || !actual.Entries.SequenceEqual(installed.Snapshot.Entries)) throw Invalid();
        PhysicalTreeIdentity.RequireSingleLinks(plan.TargetPath, actual);
        var objects = PhysicalTreeIdentity.Capture(plan.TargetPath, actual);
        var root = objects.Single(item => item.RelativePath.Length == 0).Identity;
        var held = new MaintenanceStartupInputs(plan, source, manifest, root, row, objects, head);
        try
        {
            if (restored)
            {
                var evidence = RestoredStartupEvidence.Hold(instance, database, contracts, roots, verifier, binding);
                held.Restored = evidence; held._held.Add(evidence);
                if (!objects.SequenceEqual(plan.Before.OriginalObjects)) throw Invalid();
            }
            held._held.Add(TreeReadLease.AcquireApplication(plan.TargetPath, plan.ParentIdentity, installed with { OriginalObjects = objects }));
            foreach (var reference in UpdateSourceCodec.References(source).Append(row.Plan).Append(row.Source)
                .Concat(plan.Before.Contents).Concat(plan.Candidate.Contents).Distinct()) held._held.Add(blobs.OpenVerified(reference));
            held.RequireCurrent(database, binding);
            return held;
        }
        catch { held.Dispose(); throw; }
    }

    internal void RequireCurrent(StateDatabase database, MaintenanceStartupBinding binding)
    {
        if (_held.Count == 0 || ReadOperation(database, binding) != Operation || PhysicalObjectIdentity.Read(Plan.TargetPath) != InstalledRoot) throw Invalid();
        Restored?.RequireCurrent();
        var installed = Restored is null ? Plan.Candidate : Plan.Before;
        var fresh = ApplicationTree.Capture(new(SnapshotBudget.Default), Plan.TargetPath, CancellationToken.None);
        if (fresh.Shape != installed.Snapshot.Shape || !fresh.Entries.SequenceEqual(installed.Snapshot.Entries) ||
            !PhysicalTreeIdentity.Capture(Plan.TargetPath, fresh).SequenceEqual(_installedObjects)) throw Invalid();
        PhysicalTreeIdentity.RequireSingleLinks(Plan.TargetPath, fresh);
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            UpdateFeedRows.RequireAccepted(connection, transaction, Source.Feed);
            if (UpdateFeedRows.ReadHead(connection, transaction, Source.Feed.Identity.Channel) != _acceptedHead) throw Invalid();
            transaction.Commit();
        }
        var snapshot = new RecoveryJournalRegistry(database).ReadActiveSnapshot(); RecoveryRegistryPolicy.Verify(snapshot);
        var own = snapshot.Journals.SingleOrDefault(item => item.JournalId == binding.JournalId) ?? throw Invalid();
        if (own.Kind != JournalKind.UpdateApply || own.OperationId != binding.OperationId || own.PlanPath != binding.Plan.RelativePath ||
            own.PlanDigest != binding.Plan.Sha256 || own.Phase != (Restored is null ? JournalPhase.Applied : JournalPhase.Committed) ||
            snapshot.Journals.Any(item => item.JournalId != binding.JournalId && item.Phase != JournalPhase.Committed) ||
            snapshot.Isolations.Any(item => item.JournalId != binding.JournalId) ||
            new JournalEffectGate(database).Read(binding.JournalId).State != EffectGateState.Entered) throw Invalid();
    }

    private static MaintenanceOperationRow ReadOperation(StateDatabase database, MaintenanceStartupBinding binding)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var row = MaintenanceOperationRows.ReadAll(connection, transaction).SingleOrDefault(item => item.Identity.OperationId == binding.OperationId) ?? throw Invalid();
        if (row.State != HostUpdateState.Starting &&
            !(row.State == HostUpdateState.Completed && row.TerminalCode == "maintenance_original_restored") ||
            row.HandoffId != binding.HandoffId || row.InitialJournalId != binding.JournalId ||
            row.Plan != binding.Plan || row.Source != binding.Source) throw Invalid();
        transaction.Commit(); return row;
    }

    private static byte[] Read(ImmutableBlobStore blobs, BlobReference reference)
    {
        using var input = blobs.OpenVerified(reference); var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }

    public void Dispose()
    {
        for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        _held.Clear();
    }
    private static IOException Invalid() => new("maintenance_startup_inputs_unbound");
}
