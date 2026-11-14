using System.Runtime.Versioning;
using Microsoft.Win32;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Maintenance;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal sealed class InstallerRegistration(InstallerPaths paths, string profile, string workerPath)
{
    private readonly InstallerCheckpointStore _store = new(paths, profile);
    private string CheckpointRoot => Path.Combine(paths.ManagerRoot, "metadata-checkpoint");

    internal void RecoverTransition(string root, ProcessIdentity setup, string workRoot)
    {
        InstallerCheckpointStore.RequireCaller(setup);
        using var gate = InstallerRuntimeGate.Acquire(root);
        PhysicalPathPolicy.NormalizeWithoutLinks(CheckpointRoot);
        if (File.Exists(CheckpointRoot)) throw InstallationIdentityCodec.Invalid();
        if (!Directory.Exists(CheckpointRoot)) return;
        var frame = _store.ReadPending(root);
        InstallerSourceBinding.Require(frame);
        if (frame.WorkerSha256 != RequireWorker()) throw InstallationIdentityCodec.Invalid();
        new InstallerTransition(paths, profile).RecoverStopped(frame, setup, workRoot);
    }

    internal InstallerCheckpoint BeginTransition(string root, Guid transactionId, InstallerTargetManifest target, ProcessIdentity setup, string workRoot)
    {
        InstallerCheckpointStore.RequireCaller(setup);
        if (target.Profile != profile || transactionId == Guid.Empty) throw InstallationIdentityCodec.Invalid();
        var worker = RequireWorker();
        var pending = Directory.Exists(CheckpointRoot) ? _store.RequireCurrentSetup(root, transactionId) : null;
        if (pending is not null && (pending.Stage != "prepared" || pending.WorkerSha256 != worker || pending.SetupOwner != setup ||
            pending.ManifestSha256 != target.Sha256 || pending.TargetManifest is null ||
            !target.Bytes.Span.SequenceEqual(pending.TargetManifest))) throw InstallationIdentityCodec.Invalid();
        return new InstallerOwnedRuntime(paths, profile).Use(root, setup, workRoot, source =>
        {
            if (pending is null) return _store.Create(root, transactionId, target.Sha256, setup, worker, target, source);
            InstallerSourceBinding.Require(pending);
            if (!InstallerSourceBinding.Equal(source, pending.OwnedSource)) throw InstallationIdentityCodec.Invalid();
            return pending;
        }, checkpoint: pending is not null);
    }

    internal void CancelTransition(string root, Guid transactionId, string workRoot)
    {
        var frame = _store.RequireCurrentSetup(root, transactionId);
        InstallerSourceBinding.Require(frame);
        if (frame.TargetApplication is not null)
        {
            if (frame.WorkerSha256 != RequireWorker()) throw InstallationIdentityCodec.Invalid();
            new InstallerTransition(paths, profile).Recover(root, transactionId, workRoot);
            return;
        }
        if (frame.Stage is not ("prepared" or "payload") || frame.WorkerSha256 != RequireWorker() ||
            File.Exists(Path.Combine(CheckpointRoot, "worker.pending")) || File.Exists(Path.Combine(CheckpointRoot, "identity.pending")))
            throw InstallationIdentityCodec.Invalid();
        if (frame.PayloadIdentity is not null)
        {
            var runtime = new InstallerRetainedRuntime(paths).RequireRuntime(frame.OwnedSource!.Identity);
            using var lease = InstanceLease.AcquireInstaller(runtime, new ContractReader(), frame.SetupOwner);
            _ = new InstallerRuntimeAdmission(runtime, new(), new(root, Path.Combine(runtime.RuntimeRoot, "extensions"))).Open(lease, workRoot);
            new InstallerPayloadTree(frame).RestoreOriginal();
            new InstallerPayloadTree(frame).CompleteRestoration();
        }
        else if (File.Exists(InstallerPayloadTree.Workspace(frame)) || Directory.Exists(InstallerPayloadTree.Workspace(frame)))
            throw InstallationIdentityCodec.Invalid();
        _ = new InstallerOwnedRuntime(paths, profile).Use(root, frame.SetupOwner, workRoot, source =>
        {
            if (!InstallerSourceBinding.Equal(source, frame.OwnedSource)) throw InstallationIdentityCodec.Invalid();
            new InstallerMetadataRecovery(paths, _store).RestoreCurrentMetadata(root, transactionId);
            new InstallerCheckpointCompletion(paths, _store).CompleteCurrent(root, transactionId, committed: false);
            return 0;
        }, checkpoint: true);
    }

    internal void PreserveTransition(string root, Guid transactionId, string workRoot)
    {
        var frame = _store.RequireCurrentSetup(root, transactionId);
        InstallerSourceBinding.Require(frame);
        if (frame.WorkerSha256 != RequireWorker() || frame.Stage is not ("prepared" or "payload")) throw InstallationIdentityCodec.Invalid();
        if (frame.Stage == "prepared")
            frame = new InstallerOwnedRuntime(paths, profile).Use(root, frame.SetupOwner, workRoot, source =>
            {
                if (!InstallerSourceBinding.Equal(source, frame.OwnedSource)) throw InstallationIdentityCodec.Invalid();
                return _store.PreparePayload(root, transactionId);
            }, checkpoint: true);
        var runtime = new InstallerRetainedRuntime(paths).RequireRuntime(frame.OwnedSource!.Identity);
        using var lease = InstanceLease.AcquireInstaller(runtime, new ContractReader(), frame.SetupOwner);
        _ = new InstallerRuntimeAdmission(runtime, new(), new(root, Path.Combine(runtime.RuntimeRoot, "extensions"))).Open(lease, workRoot);
        new InstallerPayloadTree(frame).PreserveOriginal();
    }

    internal void Begin(string root, Guid transactionId, InstallerTargetManifest target, ProcessIdentity setup, string? workRoot = null)
    {
        InstallerCheckpointStore.RequireCaller(setup);
        if (target.Profile != profile) throw InstallationIdentityCodec.Invalid();
        var manifestSha256 = target.Sha256;
        var workerSha256 = RequireWorker();
        if (Directory.Exists(CheckpointRoot))
        {
            var pending = _store.RequireCurrentSetup(root, transactionId);
            var original = InstallerSnapshotCodec.Decode(pending.Before, root);
            if (pending.SetupOwner != setup || pending.Stage != "prepared" || pending.ManifestSha256 != manifestSha256 || pending.WorkerSha256 != workerSha256 ||
                PhysicalObjectIdentity.Read(root) != original.RootIdentity || pending.TargetManifest is null ||
                !target.Bytes.Span.SequenceEqual(pending.TargetManifest)) throw InstallationIdentityCodec.Invalid();
            if (original.Ownership.Exists)
            {
                var retained = new InstallerRetainedRuntime(paths);
                retained.RequireDestination(InstallationDestination.Inspect(root, paths, InstallerPaths.ProtectedRoots()), checkpoint: true);
                retained.Admit(retained.Read(pending), setup, workRoot);
            }
            else if (original.Uninstall.Exists || original.Files.Length != 0) throw InstallationIdentityCodec.Invalid();
            var expected = original with { Directories = original.Directories.Select(directory => directory.Role == "manager" && directory.Identity is null
                ? directory with { Identity = pending.ManagerIdentity } : directory).ToArray() };
            if (!InstallerMetadataInventory.Equal(expected, _store.Capture(root))) throw InstallationIdentityCodec.Invalid();
            return;
        }
        var destination = InstallationDestination.Inspect(root, paths, InstallerPaths.ProtectedRoots());
        if (destination.Kind == InstallationDestinationKind.Owned || transactionId == Guid.Empty ||
            !InstallationIdentityCodec.Digest(manifestSha256)) throw InstallationIdentityCodec.Invalid();
        var metadata = InstallerMetadataInventory.Capture(destination.Root, paths);
        if (destination.Kind == InstallationDestinationKind.Retained)
        {
            var retained = new InstallerRetainedRuntime(paths);
            retained.RequireDestination(destination);
            retained.Admit(destination.Identity!, setup, workRoot);
        }
        else if (metadata.Uninstall.Exists || metadata.Ownership.Exists || metadata.Files.Length != 0)
            throw InstallationIdentityCodec.Invalid();
        Directory.CreateDirectory(destination.Root);
        _store.Create(destination.Root, transactionId, manifestSha256, setup, workerSha256, target);
    }

    internal void ApplyTransition(string root, Guid transactionId, string payloadRoot, string workRoot)
    {
        var frame = _store.RequireCurrentSetup(root, transactionId);
        if (frame.WorkerSha256 != RequireWorker()) throw InstallationIdentityCodec.Invalid();
        new InstallerTransition(paths, profile).Apply(root, transactionId, payloadRoot, workRoot);
    }

    internal InstallationIdentity Register(string root, Guid transactionId, string? initialExtensions = null)
    {
        if (!Directory.Exists(CheckpointRoot))
        {
            var existing = new InstallationAnchor(paths.ManagerRoot, paths.RegistryKey).Read(root);
            if (transactionId == Guid.Empty || existing is not { State: "active" } || existing.Worker.Sha256 != RequireWorker())
                throw InstallationIdentityCodec.Invalid();
            using var installedPayload = InstallationPayload.Open(root, existing.ManifestSha256, profile);
            RequireNative(InstallerMetadataInventory.Capture(root, paths), existing.ProductVersion);
            return existing;
        }
        var frame = _store.RequireCurrentSetup(root, transactionId);
        if (RequireWorker() != frame.WorkerSha256) throw InstallationIdentityCodec.Invalid();
        var before = InstallerSnapshotCodec.Decode(frame.Before, root);
        var original = Original(frame);
        using var payload = InstallationPayload.Open(root, frame.ManifestSha256, profile);
        InstallerTargetManifest.Read(frame).RequireInstalled(payload.Snapshot);
        if (payload.RootIdentity != before.RootIdentity) throw InstallationIdentityCodec.Invalid();
        if (frame.Stage == "prepared") Prepare(frame, payload);
        frame = _store.RequireCurrentSetup(root, transactionId);
        var publication = new InstallerIdentityPublication(paths);
        var identity = publication.Read(frame, original: false);
        if (original is not null && (identity.InstallationId != original.InstallationId || identity.RuntimeId != original.RuntimeId ||
            identity.RuntimeIdentity != original.RuntimeIdentity || identity.CreatedAt != original.CreatedAt)) throw InstallationIdentityCodec.Invalid();
        var runtime = new InstanceRoot(new ContractReader()).OpenExisting(Path.Combine(root, "runtime"));
        if (runtime.InstanceId != identity.RuntimeId || PhysicalObjectIdentity.Read(runtime.RuntimeRoot) != identity.RuntimeIdentity)
            throw InstallationIdentityCodec.Invalid();
        using var lease = InstanceLease.AcquireInstaller(runtime, new ContractReader(), frame.SetupOwner);
        if (initialExtensions is not null && original is null)
            InstallerInitialExtensions.InstallAsync(runtime, lease, root, initialExtensions).GetAwaiter().GetResult();
        publication.Publish(frame);
        _store.Observe(root, transactionId);
        var installed = new InstallationAnchor(paths.ManagerRoot, paths.RegistryKey).Read(root);
        if (installed != identity) throw InstallationIdentityCodec.Invalid();
        new InstallerCheckpointCompletion(paths, _store).CompleteCurrent(root, transactionId, committed: true);
        return identity;
    }

    internal void PrepareRegistration(string root, Guid transactionId)
    {
        var frame = _store.RequireCurrentSetup(root, transactionId);
        var before = InstallerSnapshotCodec.Decode(frame.Before, root);
        if (frame.Stage != "prepared")
            throw InstallationIdentityCodec.Invalid();
        _ = Original(frame);
        using var payload = InstallationPayload.Open(root, frame.ManifestSha256, profile);
        InstallerTargetManifest.Read(frame).RequireInstalled(payload.Snapshot);
        if (payload.RootIdentity != before.RootIdentity) throw InstallationIdentityCodec.Invalid();
        Prepare(frame, payload);
    }

    internal void CancelPrepared(string root, Guid transactionId)
    {
        var frame = _store.RequireCurrentSetup(root, transactionId);
        if (frame.Stage != "prepared" || File.Exists(Path.Combine(CheckpointRoot, "worker.pending")) ||
            File.Exists(Path.Combine(CheckpointRoot, "identity.pending"))) throw InstallationIdentityCodec.Invalid();
        var before = InstallerSnapshotCodec.Decode(frame.Before, root);
        var original = Original(frame);
        if (original is not null)
        {
            var retained = new InstallerRetainedRuntime(paths);
            retained.RequireManager(checkpoint: true);
            retained.RequireRuntime(original);
            if (ApplicationTree.Roots.Any(name => File.Exists(Path.Combine(root, name)) || Directory.Exists(Path.Combine(root, name))) ||
                !InstallerMetadataInventory.Equal(before, _store.Capture(root))) throw InstallationIdentityCodec.Invalid();
        }
        else if (Directory.EnumerateFileSystemEntries(root).Any()) throw InstallationIdentityCodec.Invalid();
        new InstallerMetadataRecovery(paths, _store).RestoreCurrentMetadata(root, transactionId);
        new InstallerCheckpointCompletion(paths, _store).CompleteCurrent(root, transactionId, committed: false);
    }

    private void Prepare(InstallerCheckpoint frame, InstallationPayload payload)
    {
        var original = Original(frame);
        var metadata = InstallerMetadataInventory.Capture(frame.Root, paths);
        if (original is null && (metadata.Ownership.Exists || metadata.Files.Any(file => file.Role == "manager") ||
            Directory.EnumerateFileSystemEntries(paths.ManagerRoot).Any(path => path != CheckpointRoot)))
            throw InstallationIdentityCodec.Invalid();
        if (original is not null)
        {
            var before = InstallerSnapshotCodec.Decode(frame.Before, frame.Root);
            new InstallerRetainedRuntime(paths).RequireManager(checkpoint: true);
            if (!InstallerMetadataInventory.Equal(before, before with { Files = metadata.Files.Where(file => file.Role == "manager").ToArray(),
                Ownership = metadata.Ownership })) throw InstallationIdentityCodec.Invalid();
        }
        RequireNative(metadata, payload.Manifest.HostVersion);
        var runtimeRoot = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(frame.Root, "runtime"));
        if (original is null && (File.Exists(runtimeRoot) || Directory.Exists(runtimeRoot))) throw InstallationIdentityCodec.Invalid();
        var runtime = original is null ? new InstanceRoot(new ContractReader()).OpenInstaller(runtimeRoot, frame.SetupOwner)
            : new InstallerRetainedRuntime(paths).RequireRuntime(original);
        using var lease = InstanceLease.AcquireInstaller(runtime, new ContractReader(), frame.SetupOwner);
        var pendingPath = Path.Combine(CheckpointRoot, "worker.pending");
        using (var source = new FileStream(workerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(pendingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { source.CopyTo(output); output.Flush(flushToDisk: true); }
        var worker = InstallerMetadataInventory.CaptureFile("manager", pendingPath);
        var engine = metadata.Files.Single(file => file.Role == "uninstall" && file.Name == "unins000.exe");
        var identity = new InstallationIdentity("nexus.installation", 1, "g0180", original?.InstallationId ?? frame.TransactionId, InstallationIdentityCodec.UserSid,
            frame.Root, payload.RootIdentity, runtime.InstanceId, PhysicalObjectIdentity.Read(runtimeRoot), payload.ManifestSha256,
            payload.Manifest.HostVersion, "active", original?.CreatedAt ?? DateTimeOffset.UtcNow, Proof(worker), Proof(engine));
        var files = metadata.Files.Where(file => file.Role != "manager" || file.Name != InstallationAnchor.WorkerFile)
            .Append(worker with { Name = InstallationAnchor.WorkerFile, ParentIdentity = frame.ManagerIdentity }).ToArray();
        var target = new InstallerIdentityPublication(paths).Stage(frame, identity, metadata with { Files = files });
        _store.Plan(frame.Root, frame.TransactionId, target);
    }

    private InstallationIdentity? Original(InstallerCheckpoint frame)
    {
        var before = InstallerSnapshotCodec.Decode(frame.Before, frame.Root);
        if (before.Ownership.Exists) return new InstallerRetainedRuntime(paths).Read(frame);
        if (before.Uninstall.Exists || before.Files.Length != 0) throw InstallationIdentityCodec.Invalid();
        return null;
    }

    internal static void RequireNative(InstallerMetadataSnapshot metadata, string version)
    {
        if (!metadata.Uninstall.Exists || !metadata.Files.Any(file => file.Role == "uninstall" && file.Name == "unins000.exe"))
            throw InstallationIdentityCodec.Invalid();
        var values = metadata.Uninstall.Values.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        if (values.GetValueOrDefault("InstallLocation") is not { Kind: RegistryValueKind.String, Text: { } location } ||
            !StringComparer.OrdinalIgnoreCase.Equals(Path.TrimEndingDirectorySeparator(location), metadata.Root) ||
            values.GetValueOrDefault("DisplayVersion") is not { Kind: RegistryValueKind.String, Text: { } installed } || installed != version ||
            values.GetValueOrDefault("UninstallString") is not { Kind: RegistryValueKind.String, Text: { } command } ||
            !StringComparer.OrdinalIgnoreCase.Equals(command, "\"" + Path.Combine(metadata.Root, InstallationAnchor.EngineEntry).Replace('/', '\\') + "\""))
            throw InstallationIdentityCodec.Invalid();
    }

    private static InstallerFile Proof(InstallerMetadataFile file) => new(file.SizeBytes, file.Sha256, file.Identity);

    private string RequireWorker()
    {
        PhysicalPathPolicy.NormalizeWithoutLinks(workerPath);
        PhysicalObjectIdentity.RequireSingleLink(workerPath);
        using var source = new FileStream(workerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        UpdateBuildProfile.RequireBundle(source, "NexusPipeline.Installer.dll", profile);
        source.Position = 0;
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(source));
    }
}
