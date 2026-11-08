using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Settings;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Windows;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal sealed class InstallerUninstallation(InstallerPaths paths, string profile, IStartupRegistrationEffects? startupEffects = null)
{
    private readonly InstallerCheckpointStore _store = new(paths, profile, "ownership");
    private readonly InstallerDataStore _data = new(paths, profile);

    internal (string GuardName, bool PendingData) Check(string root, ProcessIdentity native)
    {
        if (_data.Exists)
        {
            var intent = ReadDataHandoff(root, native, finishing: false);
            _data.RequireStopped(intent, native);
            using var original = InstallationPayload.Open(root, InstallationIdentityCodec.Unseal(intent.Installation, root).ManifestSha256, profile);
            InstallerRuntimeRemoval.Verify(intent);
            return (InstallerRuntimeGate.Name(root), true);
        }
        var (identity, _) = ReadHandoff(root, native);
        RequireStopped(identity.ApplicationRoot);
        using var payload = InstallationPayload.Open(root, identity.ManifestSha256, profile);
        var instance = Runtime(identity);
        using var lease = InstanceLease.AcquireInstaller(instance, new ContractReader(), native);
        return (InstallerRuntimeGate.Name(root), false);
    }

    internal void DeleteData(string root, Guid transactionId, ProcessIdentity native, string workRoot)
    {
        if (transactionId == Guid.Empty) throw InstallationIdentityCodec.Invalid();
        InstallerRuntimeGate.RequireHeld(root);
        if (_data.Exists)
        {
            var saved = ReadDataHandoff(root, native, finishing: false);
            using var original = InstallationPayload.Open(root, InstallationIdentityCodec.Unseal(saved.Installation, root).ManifestSha256, profile);
            _data.Claim(saved, transactionId, native);
            _data.RequireCurrent(saved, transactionId, native);
            InstallerRuntimeRemoval.Remove(saved);
            return;
        }
        var (identity, pending) = ReadHandoff(root, native);
        RequireStopped(root);
        using var payload = InstallationPayload.Open(root, identity.ManifestSha256, profile);
        var instance = Runtime(identity);
        using var lease = InstanceLease.AcquireInstaller(instance, new ContractReader(), native);
        var ready = new InstallerRuntimeAdmission(instance, new(), new(root, Path.Combine(instance.RuntimeRoot, "extensions"))).Open(lease, workRoot);
        if (pending is not null)
        {
            var recovery = new InstallerMetadataRecovery(paths, _store);
            var completion = new InstallerCheckpointCompletion(paths, _store);
            if (pending.SetupOwner == native)
            {
                recovery.RestoreCurrentMetadata(root, pending.TransactionId);
                completion.CompleteCurrent(root, pending.TransactionId, committed: false);
            }
            else
            {
                recovery.RestoreStoppedMetadata(root, pending.TransactionId);
                completion.CompleteStopped(root, pending.TransactionId);
            }
            identity = new InstallationAnchor(paths.ManagerRoot, paths.RegistryKey).Read(root) ?? throw InstallationIdentityCodec.Invalid();
        }
        DisableStartup(identity, instance, lease, ready, transactionId);
        if (ready?.Settings.Values.AutoStart == true)
            _ = new InstallerRuntimeAdmission(instance, new(), new(root, Path.Combine(instance.RuntimeRoot, "extensions")))
                .Open(lease, Path.Combine(workRoot, "after-startup"));
        // The native guard keeps normal Host admission closed while the runtime lease is released for removal.
        lease.Dispose();
        lease.WhenExecutionPublicationsReleased.GetAwaiter().GetResult();
        var frozen = InstallerRuntimeRemoval.Freeze(identity);
        var intent = _data.Create(root, transactionId, identity, _store.Capture(root), frozen.Tree, frozen.Objects, native);
        _data.RequireCurrent(intent, transactionId, native);
        InstallerRuntimeRemoval.Remove(intent);
    }

    internal void FinishData(string root, Guid transactionId, ProcessIdentity native)
    {
        InstallerRuntimeGate.RequireHeld(root);
        var intent = ReadDataHandoff(root, native, finishing: true);
        InstallerRuntimeRemoval.Verify(intent, removed: true);
        RequireNativeRemoval(root, intent);
        _data.Claim(intent, transactionId, native);
        _data.RequireCurrent(intent, transactionId, native);
        var before = InstallerSnapshotCodec.Decode(intent.Metadata, root);
        var files = before.Files.Where(file => file.Role == "manager").ToArray();
        var held = new List<FileStream>();
        try
        {
            foreach (var file in files)
            {
                var path = Path.Combine(paths.ManagerRoot, file.Name);
                if (File.Exists(path)) held.Add(InstallerMetadataBackup.HoldFile(path, file, intent.ManagerIdentity));
            }
            InstallerRegistryEffects.RequireRestorable(paths.RegistryKey, new(false, []), before.Ownership);
            InstallerRegistryEffects.Restore(paths.RegistryKey, new(false, []), before.Ownership);
            foreach (var file in files)
            {
                var path = Path.Combine(paths.ManagerRoot, file.Name);
                if (File.Exists(path)) PhysicalObjectDeletion.DeleteVerified(new(path, intent.ManagerIdentity, file.Identity,
                    NexusPipeline.Core.Configuration.FileObjectKind.File, file.SizeBytes, file.Sha256));
            }
        }
        finally { foreach (var item in held) item.Dispose(); }
        _data.Clear(intent);
        PhysicalObjectDeletion.DeleteVerified(new(paths.ManagerRoot, PhysicalObjectIdentity.Read(Path.GetDirectoryName(paths.ManagerRoot)!),
            intent.ManagerIdentity, NexusPipeline.Core.Configuration.FileObjectKind.Directory, 0, null));
    }

    internal InstallationIdentity KeepData(string root, Guid transactionId, ProcessIdentity native, string workRoot)
    {
        if (transactionId == Guid.Empty) throw InstallationIdentityCodec.Invalid();
        var (identity, pending) = ReadHandoff(root, native);
        InstallerRuntimeGate.RequireHeld(root);
        RequireStopped(identity.ApplicationRoot);
        using var payload = InstallationPayload.Open(root, identity.ManifestSha256, profile);
        var instance = Runtime(identity);
        using var lease = InstanceLease.AcquireInstaller(instance, new ContractReader(), native);
        var ready = new InstallerRuntimeAdmission(instance, new ContractReader(), new(root, Path.Combine(instance.RuntimeRoot, "extensions"))).Open(lease, workRoot);
        if (pending is not null && (pending.SetupOwner != native || pending.TransactionId != transactionId))
        {
            _store.RequireStopped(root, pending.TransactionId);
            new InstallerMetadataRecovery(paths, _store).RestoreStoppedMetadata(root, pending.TransactionId);
            new InstallerCheckpointCompletion(paths, _store).CompleteStopped(root, pending.TransactionId);
            identity = new InstallationAnchor(paths.ManagerRoot, paths.RegistryKey).Read(root) ?? throw InstallationIdentityCodec.Invalid();
            pending = null;
        }
        DisableStartup(identity, instance, lease, ready, transactionId);
        if (pending is null && identity.State == "retained-data") return identity;
        var frame = pending ?? _store.Create(root, transactionId, identity.ManifestSha256, native);
        var publication = new InstallerIdentityPublication(paths);
        if (frame.Stage == "prepared")
        {
            var target = publication.Stage(frame, identity with { State = "retained-data" }, _store.Capture(root));
            frame = _store.Plan(root, transactionId, target);
        }
        var retained = publication.Read(frame, original: false);
        if (retained != (identity with { State = "retained-data" })) throw InstallationIdentityCodec.Invalid();
        _store.RequireCurrentSetup(root, transactionId);
        publication.Publish(frame);
        _store.Observe(root, transactionId);
        if (new InstallationAnchor(paths.ManagerRoot, paths.RegistryKey).Read(root) != retained) throw InstallationIdentityCodec.Invalid();
        new InstallerCheckpointCompletion(paths, _store).CompleteCurrent(root, transactionId, committed: true);
        return retained;
    }

    private (InstallationIdentity Identity, InstallerCheckpoint? Pending) ReadHandoff(string root, ProcessIdentity native)
    {
        if (_data.Exists) throw new IOException("installer_data_removal_pending");
        InstallerCheckpointStore.RequireCaller(native);
        InstallerCheckpoint? pending = null;
        InstallationIdentity identity;
        if (Directory.Exists(Path.Combine(paths.ManagerRoot, "metadata-checkpoint")))
        {
            pending = _store.ReadPending(root);
            identity = new InstallerIdentityPublication(paths).Read(pending, original: true);
            if (identity.State != "active" || pending.ManifestSha256 != identity.ManifestSha256 || pending.WorkerSha256 != identity.Worker.Sha256 ||
                pending.SetupOwner.ImageSha256 != identity.NativeEngine.Sha256) throw InstallationIdentityCodec.Invalid();
            if (pending.After is not null && new InstallerIdentityPublication(paths).Read(pending, original: false) != (identity with { State = "retained-data" }))
                throw InstallationIdentityCodec.Invalid();
        }
        else identity = new InstallationAnchor(paths.ManagerRoot, paths.RegistryKey).Read(root) ?? throw InstallationIdentityCodec.Invalid();
        RequireHandoff(identity, native, pending is not null, data: false, finishing: false);
        return (identity, pending);
    }

    private void RequireHandoff(InstallationIdentity identity, ProcessIdentity native, bool metadata, bool data, bool finishing)
    {
        InstallerCheckpointStore.RequireCaller(native);
        foreach (var path in Directory.EnumerateFileSystemEntries(paths.ManagerRoot))
            if (Path.GetFileName(path) is not (InstallationAnchor.IdentityFile or InstallationAnchor.WorkerFile) &&
                !(metadata && Path.GetFileName(path) == "metadata-checkpoint" && Directory.Exists(path)) &&
                !(data && Path.GetFileName(path) == InstallerDataStore.DirectoryName && Directory.Exists(path)))
                throw InstallationIdentityCodec.Invalid();
        var self = ProcessIdentityReader.Read(Environment.ProcessId);
        if (self.ImageSha256 != identity.Worker.Sha256 || native.ImageSha256 != identity.NativeEngine.Sha256)
            throw new IOException("installer_native_handoff_unverified");
        var worker = Path.Combine(paths.ManagerRoot, InstallationAnchor.WorkerFile);
        var engine = Path.Combine(identity.ApplicationRoot, InstallationAnchor.EngineEntry);
        if (!finishing || File.Exists(worker) || Directory.Exists(worker)) RequireFile(worker, identity.Worker);
        if (!finishing || File.Exists(engine) || Directory.Exists(engine)) RequireFile(engine, identity.NativeEngine);
        if (Directory.Exists(identity.ApplicationRoot) ? PhysicalObjectIdentity.Read(identity.ApplicationRoot) != identity.RootIdentity :
            !finishing || File.Exists(identity.ApplicationRoot)) throw InstallationIdentityCodec.Invalid();
    }

    private InstallerDataIntent ReadDataHandoff(string root, ProcessIdentity native, bool finishing)
    {
        var intent = _data.Read(root);
        var identity = InstallationIdentityCodec.Unseal(intent.Installation, root);
        RequireHandoff(identity, native, metadata: false, data: true, finishing);
        RequireStopped(root);
        var before = InstallerSnapshotCodec.Decode(intent.Metadata, root);
        var source = Path.Combine(paths.ManagerRoot, InstallationAnchor.IdentityFile);
        var seal = before.Files.Single(file => file.Role == "manager" && file.Name == InstallationAnchor.IdentityFile);
        if (!finishing || File.Exists(source) || Directory.Exists(source))
        {
            using var held = InstallerMetadataBackup.HoldFile(source, seal, intent.ManagerIdentity);
        }
        var ownership = InstallerMetadataInventory.CaptureRegistry(paths.RegistryKey);
        if (!finishing && !InstallerMetadataInventory.Equal(before, before with { Ownership = ownership }))
            throw InstallationIdentityCodec.Invalid();
        return intent;
    }

    private void RequireNativeRemoval(string root, InstallerDataIntent intent)
    {
        if (ApplicationTree.Roots.Any(name => File.Exists(Path.Combine(root, name)) || Directory.Exists(Path.Combine(root, name))) ||
            Directory.Exists(Path.Combine(root, "uninstall")) || File.Exists(Path.Combine(root, "uninstall")) ||
            InstallerMetadataInventory.CaptureRegistry(paths.UninstallRegistryKey).Exists)
            throw new IOException("installer_native_removal_pending");
        var before = InstallerSnapshotCodec.Decode(intent.Metadata, root);
        foreach (var file in before.Files.Where(file => file.Role is "desktop" or "group"))
        {
            var path = Path.Combine(InstallerMetadataInventory.DirectoryPath(root, paths, file.Role), file.Name);
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("installer_native_removal_pending");
        }
    }

    private static RuntimeInstance Runtime(InstallationIdentity identity)
    {
        var root = Path.Combine(identity.ApplicationRoot, "runtime");
        var instance = new InstanceRoot(new ContractReader()).OpenExisting(root);
        if (instance.InstanceId != identity.RuntimeId || PhysicalObjectIdentity.Read(root) != identity.RuntimeIdentity ||
            !File.Exists(Path.Combine(root, "instance.lock"))) throw InstallationIdentityCodec.Invalid();
        return instance;
    }

    private static void RequireFile(string path, InstallerFile expected)
    {
        var proof = InstallerMetadataInventory.CaptureFile("handoff", path);
        if (proof.Identity != expected.Identity || proof.SizeBytes != expected.SizeBytes || proof.Sha256 != expected.Sha256)
            throw InstallationIdentityCodec.Invalid();
    }

    private static void RequireStopped(string root)
    {
        foreach (var name in new[] { ApplicationTree.HostEntry, ApplicationTree.LauncherEntry, ApplicationTree.MaintenanceEntry, ApplicationTree.DesktopEntry })
            ExclusiveProgramAdmission.RequireStopped(Path.Combine(root, name));
    }

    private void DisableStartup(InstallationIdentity identity, RuntimeInstance instance, InstanceLease lease,
        InstallerReadyRuntime? ready, Guid transactionId)
    {
        if (ready is null) return;
        var saved = ready.Settings;
        if (!saved.Values.AutoStart) return;
        var resources = new ResourceLeaseManager(new HostLifecycle(ready.Recovery, TimeProvider.System));
        var settings = HostStartupSettings.CreateForInstallation(instance, ready.Database, lease, resources, ready.Recovery,
            Path.Combine(identity.ApplicationRoot, ApplicationTree.HostEntry), startupEffects ?? new StartupRegistrationEffects());
        var change = new HostSettingsChange(saved.Revision, saved.Values with { AutoStart = false });
        var operation = HostSettingsStore.Identity(transactionId, "installer:" + identity.InstallationId.ToString("D"), change);
        var result = settings.Save(operation, change, DateTimeOffset.UtcNow);
        if (result.StartupEffect?.State != StartupEffectState.Completed) throw new IOException("installer_startup_removal_unverified");
    }
}
