using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal sealed class InstallerTransition(InstallerPaths paths, string profile)
{
    private readonly InstallerCheckpointStore _store = new(paths, profile);

    internal void Apply(string root, Guid transactionId, string payloadRoot, string workRoot)
    {
        var frame = _store.RequireCurrentSetup(root, transactionId);
        InstallerSourceBinding.Require(frame);
        if (frame.Stage == "payload") new InstallerOwnedRuntime(paths, profile).RequireExternalRoot(frame.Root, payloadRoot, workRoot);
        var runtime = new InstallerRetainedRuntime(paths).RequireRuntime(frame.OwnedSource!.Identity);
        using var lease = InstanceLease.AcquireInstaller(runtime, new ContractReader(), frame.SetupOwner);
        _ = new InstallerRuntimeAdmission(runtime, new(), new(root, Path.Combine(runtime.RuntimeRoot, "extensions"))).Open(lease, workRoot);
        if (frame.Stage == "payload")
        {
            var target = new InstallerTargetTree(frame).Stage(payloadRoot);
            try { frame = _store.FreezeTarget(root, transactionId, target); }
            catch
            {
                new InstallerTargetTree(frame with { TargetApplication = target }).RemoveTarget();
                throw;
            }
        }
        if (frame.Stage == "target")
        {
            new InstallerIdentityPublication(paths).DiscardUnplanned(frame);
            new InstallerTargetTree(frame).Install();
            using var payload = InstallationPayload.Open(root, frame.ManifestSha256, profile);
            var before = InstallerSnapshotCodec.Decode(frame.Before, root);
            if (!InstallerMetadataInventory.Equal(before, _store.Capture(root))) throw InstallationIdentityCodec.Invalid();
            var identity = frame.OwnedSource!.Identity with { ManifestSha256 = frame.ManifestSha256, ProductVersion = payload.Manifest.HostVersion };
            var metadata = before with { Uninstall = before.Uninstall with { Values = before.Uninstall.Values.Select(value =>
                value.Name == "DisplayVersion" ? value with { Text = payload.Manifest.HostVersion } : value).ToArray() } };
            var planned = new InstallerIdentityPublication(paths).Stage(frame, identity, metadata);
            frame = _store.Plan(root, transactionId, planned);
        }
        if (frame.Stage == "planned")
        {
            new InstallerTargetTree(frame).RequireInstalled();
            new InstallerIdentityPublication(paths).Publish(frame);
            frame = _store.Observe(root, transactionId);
        }
        Complete(frame);
    }

    internal void Recover(string root, Guid transactionId, string workRoot)
    {
        var frame = _store.RequireCurrentSetup(root, transactionId);
        InstallerSourceBinding.Require(frame);
        var runtime = new InstallerRetainedRuntime(paths).RequireRuntime(frame.OwnedSource!.Identity);
        using var lease = InstanceLease.AcquireInstaller(runtime, new ContractReader(), frame.SetupOwner);
        _ = new InstallerRuntimeAdmission(runtime, new(), new(root, Path.Combine(runtime.RuntimeRoot, "extensions"))).Open(lease, workRoot);
        Restore(frame, stopped: false);
    }

    internal void RecoverStopped(InstallerCheckpoint frame, ProcessIdentity setup, string workRoot)
    {
        InstallerCheckpointStore.RequireCaller(setup);
        InstallerRuntimeGate.RequireHeld(frame.Root);
        frame = _store.RequireStopped(frame.Root, frame.TransactionId);
        InstallerSourceBinding.Require(frame);
        new InstallerOwnedRuntime(paths, profile).RequireExternalRoot(frame.Root, workRoot);
        foreach (var name in new[] { ApplicationTree.HostEntry, ApplicationTree.LauncherEntry, ApplicationTree.MaintenanceEntry, ApplicationTree.DesktopEntry })
            if (File.Exists(Path.Combine(frame.Root, name))) ExclusiveProgramAdmission.RequireStopped(Path.Combine(frame.Root, name));
        var runtime = new InstallerRetainedRuntime(paths).RequireRuntime(frame.OwnedSource!.Identity);
        using var lease = InstanceLease.AcquireInstaller(runtime, new ContractReader(), setup);
        _ = new InstallerRuntimeAdmission(runtime, new(), new(frame.Root, Path.Combine(runtime.RuntimeRoot, "extensions"))).Open(lease, workRoot);
        Restore(frame, stopped: true);
    }

    private void Restore(InstallerCheckpoint frame, bool stopped)
    {
        var root = frame.Root;
        var transactionId = frame.TransactionId;
        if (frame.Stage == "observed")
        {
            Complete(frame, stopped);
            return;
        }
        if (frame.Stage == "target") new InstallerIdentityPublication(paths).DiscardUnplanned(frame);
        var recovery = new InstallerMetadataRecovery(paths, _store);
        if (stopped) recovery.RestoreStoppedMetadata(root, transactionId);
        else recovery.RestoreCurrentMetadata(root, transactionId);
        if (frame.TargetApplication is not null) new InstallerTargetTree(frame).RemoveTarget();
        if (frame.PayloadIdentity is not null)
        {
            new InstallerPayloadTree(frame).RestoreOriginal();
            new InstallerPayloadTree(frame).CompleteRestoration();
        }
        else if (File.Exists(InstallerPayloadTree.Workspace(frame)) || Directory.Exists(InstallerPayloadTree.Workspace(frame)))
            throw InstallationIdentityCodec.Invalid();
        if (stopped)
        {
            var original = frame.OwnedSource!;
            var snapshot = ApplicationTree.Capture(new FileTreeScanner(SnapshotBudget.Default), root, default);
            if (!original.Application.Entries.SequenceEqual(snapshot.Entries) ||
                !original.ApplicationObjects.SequenceEqual(PhysicalTreeIdentity.Capture(root, snapshot)))
                throw InstallationIdentityCodec.Invalid();
        }
        var completion = new InstallerCheckpointCompletion(paths, _store);
        if (stopped) completion.CompleteStopped(root, transactionId);
        else completion.CompleteCurrent(root, transactionId, committed: false);
    }

    private void Complete(InstallerCheckpoint frame, bool stopped = false)
    {
        if (frame.Stage != "observed" || frame.TargetApplication is null) throw InstallationIdentityCodec.Invalid();
        new InstallerTargetTree(frame).RequireInstalled();
        var expected = new InstallerIdentityPublication(paths).Read(frame, original: false);
        if (new InstallationAnchor(paths.ManagerRoot, paths.RegistryKey).Read(frame.Root) != expected)
            throw InstallationIdentityCodec.Invalid();
        _store.CommitTransition(frame, stopped);
        new InstallerTargetTree(frame).Complete();
        var completion = new InstallerCheckpointCompletion(paths, _store);
        if (stopped) completion.CompleteStopped(frame.Root, frame.TransactionId, committed: true);
        else completion.CompleteCurrent(frame.Root, frame.TransactionId, committed: true);
    }
}
