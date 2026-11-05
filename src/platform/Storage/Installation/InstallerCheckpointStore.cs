using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal sealed class InstallerCheckpointStore
{
    private readonly InstallerPaths _paths;
    private readonly string _profile;
    private readonly string _scope;
    private string DirectoryPath => Path.Combine(_paths.ManagerRoot, "metadata-checkpoint");

    internal InstallerCheckpointStore(InstallerPaths paths, string profile, string scope = "installation")
    {
        if (profile is not ("production" or "test")) throw new ArgumentException("installer_profile_invalid");
        if (scope is not ("installation" or "ownership")) throw new ArgumentException("installer_scope_invalid");
        _paths = paths; _profile = profile; _scope = scope;
    }

    internal InstallerMetadataSnapshot Capture(string root) => InstallerMetadataInventory.Capture(root, _paths, _scope);

    internal InstallerCheckpoint Create(string root, Guid transactionId, string manifestSha256, ProcessIdentity setup,
        string? workerSha256 = null, InstallerTargetManifest? target = null, InstallerOwnedSource? ownedSource = null)
    {
        RequireCaller(setup);
        var before = Capture(root);
        if (before.RootIdentity is null || transactionId == Guid.Empty || !InstallationIdentityCodec.Digest(manifestSha256))
            throw InstallationIdentityCodec.Invalid();
        if (target is not null && (target.Profile != _profile || target.Sha256 != manifestSha256 || _scope != "installation"))
            throw InstallationIdentityCodec.Invalid();
        if (ownedSource is not null)
        {
            if (target is null || _scope != "installation") throw InstallationIdentityCodec.Invalid();
            InstallerSourceBinding.Require(ownedSource, before, _profile, setup);
        }
        PhysicalPathPolicy.NormalizeWithoutLinks(DirectoryPath);
        if (File.Exists(DirectoryPath) || Directory.Exists(DirectoryPath)) throw InstallationIdentityCodec.Invalid();
        Directory.CreateDirectory(PhysicalPathPolicy.NormalizeWithoutLinks(_paths.ManagerRoot));
        using var manager = StableDirectoryLease.Acquire(_paths.ManagerRoot);
        ExclusiveDirectory.Create(DirectoryPath);
        using var checkpoint = StableDirectoryLease.Acquire(DirectoryPath);
        ExclusiveDirectory.Create(Path.Combine(DirectoryPath, "before"));
        var data = new InstallerCheckpoint("nexus.installer.checkpoint", 1, transactionId, "prepared", before.Root, _profile, _scope,
            manifestSha256, workerSha256 ?? ProcessIdentityReader.Read(Environment.ProcessId).ImageSha256,
            PhysicalObjectIdentity.Read(_paths.ManagerRoot), PhysicalObjectIdentity.Read(DirectoryPath),
            PhysicalObjectIdentity.Read(Path.Combine(DirectoryPath, "before")),
            setup, ProcessIdentityReader.Read(Environment.ProcessId), DateTimeOffset.UtcNow,
            InstallerSnapshotCodec.Encode(before), null, null) { TargetManifest = target?.Bytes.ToArray(), OwnedSource = ownedSource };
        SaveNew("prepared.protected", data);
        return data;
    }

    internal InstallerCheckpoint Observe(string root, Guid transactionId)
    {
        var prepared = ReadFile("prepared.protected", root, transactionId).Data;
        RequireCurrentSetup(root, transactionId);
        if (File.Exists(Path.Combine(DirectoryPath, "observed.protected")))
        {
            var existing = Read(root, transactionId);
            var current = Capture(root);
            if (!InstallerMetadataInventory.Equal(InstallerSnapshotCodec.Decode(existing.After!, root), current))
                throw InstallationIdentityCodec.Invalid();
            return existing;
        }
        var after = Capture(root);
        var predecessor = ReadFile(File.Exists(Path.Combine(DirectoryPath, "planned.protected")) ? "planned.protected" :
            File.Exists(Path.Combine(DirectoryPath, "target.protected")) ? "target.protected" :
            File.Exists(Path.Combine(DirectoryPath, "payload.protected")) ? "payload.protected" : "prepared.protected", root, transactionId);
        if (predecessor.Data.After is not null && !InstallerMetadataInventory.Equal(
            InstallerSnapshotCodec.Decode(predecessor.Data.After, root), after)) throw InstallationIdentityCodec.Invalid();
        var data = prepared with { Stage = "observed", Writer = ProcessIdentityReader.Read(Environment.ProcessId),
            After = InstallerSnapshotCodec.Encode(after), ParentSha256 = predecessor.Sha256, PayloadIdentity = predecessor.Data.PayloadIdentity,
            TargetApplication = predecessor.Data.TargetApplication };
        using var held = StableDirectoryLease.Acquire(DirectoryPath);
        SaveNew("observed.protected", data);
        return data;
    }

    internal InstallerCheckpoint Plan(string root, Guid transactionId, InstallerMetadataSnapshot target)
    {
        var current = RequireCurrentSetup(root, transactionId);
        if (current.Stage is not ("prepared" or "payload" or "target")) throw InstallationIdentityCodec.Invalid();
        var predecessor = ReadFile(current.Stage + ".protected", root, transactionId);
        var planned = current with { Stage = "planned", Writer = ProcessIdentityReader.Read(Environment.ProcessId),
            After = InstallerSnapshotCodec.Encode(target), ParentSha256 = predecessor.Sha256 };
        SaveNew("planned.protected", planned);
        return planned;
    }

    internal InstallerCheckpoint PreparePayload(string root, Guid transactionId)
    {
        var current = RequireCurrentSetup(root, transactionId);
        InstallerSourceBinding.Require(current);
        if (current.Stage != "prepared") throw InstallationIdentityCodec.Invalid();
        var predecessor = ReadFile("prepared.protected", root, transactionId);
        using var application = StableDirectoryLease.Acquire(current.Root);
        if (PhysicalObjectIdentity.Read(current.Root) != current.OwnedSource!.Identity.RootIdentity) throw InstallationIdentityCodec.Invalid();
        var workspace = InstallerPayloadTree.Workspace(current);
        ExclusiveDirectory.Create(workspace);
        var payload = current with { Stage = "payload", Writer = ProcessIdentityReader.Read(Environment.ProcessId),
            ParentSha256 = predecessor.Sha256, PayloadIdentity = PhysicalObjectIdentity.Read(workspace) };
        SaveNew("payload.protected", payload);
        return payload;
    }

    internal InstallerCheckpoint FreezeTarget(string root, Guid transactionId, InstallerApplicationTarget target)
    {
        var current = RequireCurrentSetup(root, transactionId);
        if (current.Stage != "payload") throw InstallationIdentityCodec.Invalid();
        var predecessor = ReadFile("payload.protected", root, transactionId);
        var frozen = current with { Stage = "target", Writer = ProcessIdentityReader.Read(Environment.ProcessId),
            ParentSha256 = predecessor.Sha256, TargetApplication = target };
        SaveNew("target.protected", frozen);
        return frozen;
    }

    internal InstallerCheckpoint Read(string root, Guid transactionId)
    {
        if (File.Exists(PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(DirectoryPath, "committed.protected"))))
        {
            var committed = ReadFile("committed.protected", root, transactionId).Data;
            if (committed.Stage != "observed" || committed.TargetApplication is null) throw InstallationIdentityCodec.Invalid();
            return committed;
        }
        var prepared = ReadFile("prepared.protected", root, transactionId);
        var predecessor = prepared;
        if (File.Exists(PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(DirectoryPath, "payload.protected"))))
        {
            var payload = ReadFile("payload.protected", root, transactionId);
            RequireSuccessor(predecessor, payload.Data, "payload");
            predecessor = payload;
        }
        if (File.Exists(PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(DirectoryPath, "target.protected"))))
        {
            var target = ReadFile("target.protected", root, transactionId);
            RequireSuccessor(predecessor, target.Data, "target");
            predecessor = target;
        }
        if (File.Exists(PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(DirectoryPath, "planned.protected"))))
        {
            var planned = ReadFile("planned.protected", root, transactionId);
            RequireSuccessor(predecessor, planned.Data, "planned");
            predecessor = planned;
        }
        var observedPath = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(DirectoryPath, "observed.protected"));
        if (!File.Exists(observedPath)) return predecessor.Data;
        var observed = ReadFile("observed.protected", root, transactionId).Data;
        RequireSuccessor(predecessor, observed, "observed");
        if (predecessor.Data.Stage == "planned" && !observed.After!.SequenceEqual(predecessor.Data.After!))
            throw InstallationIdentityCodec.Invalid();
        return observed;
    }

    internal InstallerCheckpoint ReadPending(string root)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(DirectoryPath,
            File.Exists(Path.Combine(DirectoryPath, "committed.protected")) ? "committed.protected" : "prepared.protected"));
        PhysicalObjectIdentity.RequireSingleLink(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > InstallerCheckpointCodec.MaximumProtectedBytes) throw InstallationIdentityCodec.Invalid();
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        var prepared = InstallerCheckpointCodec.Unseal(bytes);
        return Read(root, prepared.TransactionId);
    }

    private static void RequireSuccessor((InstallerCheckpoint Data, string Sha256) prepared, InstallerCheckpoint observed, string stage)
    {
        if (observed.Stage != stage || observed.ParentSha256 != prepared.Sha256 ||
            observed.Root != prepared.Data.Root || observed.Profile != prepared.Data.Profile || observed.Scope != prepared.Data.Scope ||
            observed.ManifestSha256 != prepared.Data.ManifestSha256 || observed.WorkerSha256 != prepared.Data.WorkerSha256 ||
            observed.ManagerIdentity != prepared.Data.ManagerIdentity ||
            observed.CheckpointIdentity != prepared.Data.CheckpointIdentity || observed.BackupIdentity != prepared.Data.BackupIdentity ||
            observed.SetupOwner != prepared.Data.SetupOwner ||
            observed.CreatedAt != prepared.Data.CreatedAt || !observed.Before.SequenceEqual(prepared.Data.Before) ||
            (stage == "payload" ? prepared.Data.PayloadIdentity is not null || observed.PayloadIdentity is null :
                observed.PayloadIdentity != prepared.Data.PayloadIdentity) ||
            (stage == "target" ? prepared.Data.TargetApplication is not null || observed.TargetApplication is null :
                !System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(observed.TargetApplication).AsSpan().SequenceEqual(
                    System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(prepared.Data.TargetApplication))) ||
            !InstallerSourceBinding.Equal(observed.OwnedSource, prepared.Data.OwnedSource) ||
            (observed.TargetManifest is null) != (prepared.Data.TargetManifest is null) ||
            observed.TargetManifest is not null && !observed.TargetManifest.AsSpan().SequenceEqual(prepared.Data.TargetManifest))
            throw InstallationIdentityCodec.Invalid();
    }

    internal InstallerCheckpoint RequireStopped(string root, Guid transactionId)
    {
        var current = Read(root, transactionId);
        foreach (var actor in Writers(root, transactionId).Append(current.SetupOwner).Distinct())
            if (WriterStopEvidence.ObserveProcess(actor) != WriterState.Stopped) throw InstallationIdentityCodec.Invalid();
        return current;
    }

    internal InstallerCheckpoint RequireCurrentSetup(string root, Guid transactionId)
    {
        var current = Read(root, transactionId);
        RequireCaller(current.SetupOwner);
        var self = ProcessIdentityReader.Read(Environment.ProcessId);
        foreach (var writer in Writers(root, transactionId).Distinct())
            if (writer != self && WriterStopEvidence.ObserveProcess(writer) != WriterState.Stopped)
                throw InstallationIdentityCodec.Invalid();
        return current;
    }

    private IEnumerable<ProcessIdentity> Writers(string root, Guid transactionId)
    {
        foreach (var name in new[] { "prepared.protected", "payload.protected", "target.protected", "planned.protected", "observed.protected", "committed.protected" })
            if (File.Exists(PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(DirectoryPath, name))))
                yield return ReadFile(name, root, transactionId).Data.Writer;
    }

    private (InstallerCheckpoint Data, string Sha256) ReadFile(string name, string root, Guid transactionId)
    {
        PhysicalPathPolicy.NormalizeWithoutLinks(_paths.ManagerRoot);
        PhysicalPathPolicy.NormalizeWithoutLinks(DirectoryPath);
        using var directory = StableDirectoryLease.Acquire(DirectoryPath);
        foreach (var entry in Directory.EnumerateFileSystemEntries(DirectoryPath))
        {
            var attributes = File.GetAttributes(entry); var item = Path.GetFileName(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (item == "before" ? (attributes & FileAttributes.Directory) == 0 :
                item is not ("prepared.protected" or "payload.protected" or "target.protected" or "planned.protected" or "observed.protected" or "committed.protected" or "worker.pending" or "identity.pending") ||
                (attributes & FileAttributes.Directory) != 0))
                throw InstallationIdentityCodec.Invalid();
        }
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(DirectoryPath, name));
        PhysicalObjectIdentity.RequireSingleLink(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > InstallerCheckpointCodec.MaximumProtectedBytes) throw InstallationIdentityCodec.Invalid();
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        var value = InstallerCheckpointCodec.Unseal(bytes);
        if (value.TransactionId != transactionId || value.Profile != _profile || value.Scope != _scope ||
            !StringComparer.OrdinalIgnoreCase.Equals(value.Root, PhysicalPathPolicy.NormalizeWithoutLinks(root)) ||
            value.ManagerIdentity != PhysicalObjectIdentity.Read(_paths.ManagerRoot) ||
            value.CheckpointIdentity != PhysicalObjectIdentity.Read(DirectoryPath) ||
            value.BackupIdentity != PhysicalObjectIdentity.Read(Path.Combine(DirectoryPath, "before")) ||
            name == "prepared.protected" && value.Stage != "prepared") throw InstallationIdentityCodec.Invalid();
        return (value, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private void SaveNew(string name, InstallerCheckpoint data)
    {
        using var directory = StableDirectoryLease.Acquire(DirectoryPath);
        if (PhysicalObjectIdentity.Read(_paths.ManagerRoot) != data.ManagerIdentity ||
            PhysicalObjectIdentity.Read(DirectoryPath) != data.CheckpointIdentity ||
            PhysicalObjectIdentity.Read(Path.Combine(DirectoryPath, "before")) != data.BackupIdentity) throw InstallationIdentityCodec.Invalid();
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(DirectoryPath, name));
        var bytes = InstallerCheckpointCodec.Seal(data);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        output.Write(bytes); output.Flush(flushToDisk: true);
    }

    internal void CommitTransition(InstallerCheckpoint frame, bool stopped = false)
    {
        if (frame.Stage != "observed" || frame.TargetApplication is null) throw InstallationIdentityCodec.Invalid();
        var current = stopped ? RequireStopped(frame.Root, frame.TransactionId) : RequireCurrentSetup(frame.Root, frame.TransactionId);
        if (current.Stage != "observed") throw InstallationIdentityCodec.Invalid();
        if (!File.Exists(Path.Combine(DirectoryPath, "committed.protected"))) SaveNew("committed.protected", current);
    }

    internal static void RequireCaller(ProcessIdentity setup)
    {
        var observed = ProcessSnapshotProbe.Capture(Environment.ProcessId, "", default);
        var self = ProcessIdentityReader.Read(Environment.ProcessId);
        if (observed.Truncated || observed.Processes.Count != 1 || observed.Processes[0].Ppid != setup.ProcessId ||
            self.UserSid != setup.UserSid || self.IsElevated != setup.IsElevated || ProcessIdentityReader.Read(setup.ProcessId) != setup)
            throw InstallationIdentityCodec.Invalid();
    }
}
