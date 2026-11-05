using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Maintenance;

[SupportedOSPlatform("windows")]
internal sealed class MaintenanceBootstrapPayload : IDisposable
{
    private readonly MaintenanceHandoffInputs _inputs;
    private readonly IReadOnlyList<PhysicalTreeObject> _objects;
    private readonly FileTreeSnapshot _snapshot;
    private TreeReadLease? _tree;
    internal string Root { get; }
    internal string HelperPath => PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(Root, _inputs.Manifest.MaintenanceEntry));
    internal MaintenanceHandoffInputs Inputs => _inputs;

    private MaintenanceBootstrapPayload(MaintenanceHandoffInputs inputs, string root, TreeReadLease tree,
        ArchivedFileTree archive)
    { _inputs = inputs; Root = root; _tree = tree; _objects = archive.OriginalObjects; _snapshot = archive.Snapshot; }

    internal static MaintenanceBootstrapPayload Prepare(PreparedUpdateHandoff handoff, InstanceLease lease,
        StateDatabase database, ContractReader contracts, PayloadApplyRoots roots, UpdateFeedVerifier verifier,
        CancellationToken cancellation)
    {
        lease.VerifyHeld(lease.Instance);
        if (!handoff.Root.IsActive || handoff.Plan.InstanceId != lease.Instance.InstanceId ||
            handoff.Drain.OperationId != handoff.Plan.OperationId) throw Invalid();
        var inputs = MaintenanceHandoffInputs.Hold(lease.Instance, database, contracts, roots, verifier,
            handoff.Plan.OperationId, handoff.HandoffId, cancellation);
        try
        {
            if (inputs.Operation.Plan != handoff.PlanReference || inputs.Source != handoff.Source) throw Invalid();
            var path = BootstrapPath(inputs); EnsureStagingParent(inputs, path);
            if (!Directory.Exists(path))
            {
                if (File.Exists(path)) throw Invalid();
                _ = new FileTreeArchive(new(SnapshotBudget.Default), new(inputs.Instance, contracts))
                    .MaterializeNew(BootstrapArchive.Select(inputs.Plan.Candidate, inputs.Manifest.MaintenanceEntry), path, cancellation);
            }
            var result = HoldTree(inputs, path, cancellation); lease.VerifyHeld(lease.Instance); return result;
        }
        catch { inputs.Dispose(); throw; }
    }

    internal static MaintenanceBootstrapPayload OpenExisting(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        PayloadApplyRoots roots, UpdateFeedVerifier verifier, Guid operationId, Guid handoffId, CancellationToken cancellation)
    {
        var inputs = MaintenanceHandoffInputs.Hold(instance, database, contracts, roots, verifier, operationId, handoffId, cancellation);
        try { return HoldTree(inputs, BootstrapPath(inputs), cancellation); }
        catch { inputs.Dispose(); throw; }
    }

    internal void RequireCurrent()
    {
        if (_tree is null) throw Invalid();
        _inputs.RequireCurrent();
        var actual = new FileTreeScanner(SnapshotBudget.Default).Capture(Root, CancellationToken.None);
        if (actual.Shape != _snapshot.Shape || !actual.Entries.SequenceEqual(_snapshot.Entries) ||
            !PhysicalTreeIdentity.Capture(Root, actual).SequenceEqual(_objects)) throw Invalid();
        PhysicalTreeIdentity.RequireSingleLinks(Root, actual);
    }

    internal void RequireHelper(ProcessIdentity actual)
    {
        RequireCurrent();
        var expected = _inputs.Manifest.Files.Single(file => file.RelativePath == _inputs.Manifest.MaintenanceEntry);
        if (!StringComparer.OrdinalIgnoreCase.Equals(actual.ImagePath, HelperPath) || actual.ImageSha256 != expected.Sha256) throw Invalid();
        using var image = new FileStream(HelperPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        UpdateBuildProfile.RequireBundle(image, "NexusPipeline.Maintenance.dll", _inputs.Manifest.Profile);
    }

    private static MaintenanceBootstrapPayload HoldTree(MaintenanceHandoffInputs inputs, string path, CancellationToken cancellation)
    {
        var expected = BootstrapArchive.Select(inputs.Plan.Candidate, inputs.Manifest.MaintenanceEntry);
        var actual = new FileTreeScanner(SnapshotBudget.Default).Capture(path, cancellation);
        if (actual.Shape != expected.Snapshot.Shape || !actual.Entries.SequenceEqual(expected.Snapshot.Entries)) throw Invalid();
        PhysicalTreeIdentity.RequireSingleLinks(path, actual);
        var archive = expected with { OriginalObjects = PhysicalTreeIdentity.Capture(path, actual) };
        var tree = TreeReadLease.Acquire(path, PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!), archive);
        try
        {
            var result = new MaintenanceBootstrapPayload(inputs, path, tree, archive);
            result.RequireCurrent(); return result;
        }
        catch { tree.Dispose(); throw; }
    }

    private static string BootstrapPath(MaintenanceHandoffInputs inputs) => PhysicalPathPolicy.NormalizeSyntax(Path.Combine(
        inputs.Instance.RuntimeRoot, "staging", "host-update-" + inputs.Plan.OperationId.ToString("N"), "bootstrap"));

    private static void EnsureStagingParent(MaintenanceHandoffInputs inputs, string target)
    {
        var staging = Path.Combine(inputs.Instance.RuntimeRoot, "staging");
        if (!Directory.Exists(staging)) ExclusiveDirectory.Create(staging);
        PhysicalPathPolicy.NormalizeWithoutLinks(staging);
        var parent = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(parent)) ExclusiveDirectory.Create(parent);
        PhysicalPathPolicy.NormalizeWithoutLinks(parent);
        foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
        {
            PhysicalPathPolicy.NormalizeWithoutLinks(entry);
            if (Path.GetFileName(entry) is not ("payload.zip" or "bootstrap")) throw Invalid();
        }
    }

    public void Dispose()
    {
        try { Interlocked.Exchange(ref _tree, null)?.Dispose(); } finally { _inputs.Dispose(); }
    }
    private static IOException Invalid() => new("maintenance_bootstrap_payload_unbound");
}
