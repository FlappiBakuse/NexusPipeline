using System.Runtime.InteropServices;
using System.Text.Json;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Updates;

internal static class InstallerMetadataCheckpoint
{
    private static string DirectoryPath => Path.Combine(InstallationOwnership.ManagerDirectory, "metadata-checkpoint");
    private static string BackupPath => Path.Combine(DirectoryPath, "uninstaller-backup");
    private static string Normalize(string root) => Path.GetFullPath(root).TrimEnd('\\');
    private static string Key(InstallerMetadataFile file) => (file.Location ?? "manager") + "/" + file.Name;
    private static IEnumerable<InstallerMetadataFile> Files(InstallerMetadataSnapshot snapshot) => snapshot.UninstallFiles.Concat(snapshot.Shortcuts ?? []);
    private static bool Equal<T>(T left, T right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
    private static string? Hash(string path)
    {
        InstallationOwnership.RequireLinkFree(path);
        return File.Exists(path) ? UpdateApply.ImageHash(path) : null;
    }
    private static string FilePath(string root, InstallerMetadataFile file)
    {
        if (file.Name != Path.GetFileName(file.Name)) throw new IOException("installer.metadata_filename");
        string folder = file.Location switch
        {
            "app" => root,
            "manager" or null => InstallationOwnership.ManagerDirectory,
            "programs" => Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "group" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "NexusPipeline"),
            "legacy-group" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "NexusPipeline " + InstallationGeneration.Id),
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            _ => throw new IOException("installer.metadata_location"),
        };
        string path = Path.Combine(folder, file.Name);
        InstallationOwnership.RequireLinkFree(path);
        return path;
    }
    private static bool UninstallerName(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, "^unins[0-9]+\\.(exe|dat|msg)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static string UninstallImage(InstallerRegistrySnapshot registry)
    {
        string command = registry.Values.SingleOrDefault(value => value.Name.Equals("UninstallString", StringComparison.OrdinalIgnoreCase))?.Text ?? "";
        if (command.Length < 3 || command[0] != '"' || command[^1] != '"' || command[1..^1].Contains('"')) throw new IOException("installer.metadata_uninstall_command");
        return Path.GetFullPath(command[1..^1]);
    }
    private static void ValidateGroup(string root, InstallerMetadataSnapshot snapshot, bool installed)
    {
        if (!snapshot.Uninstall.Exists)
        {
            if (snapshot.UninstallFiles.Length != 0) throw new IOException("installer.metadata_unanchored_uninstaller");
            return;
        }
        string image = UninstallImage(snapshot.Uninstall);
        string folder = Normalize(Path.GetDirectoryName(image)!);
        string location = folder.Equals(Normalize(root), StringComparison.OrdinalIgnoreCase) ? "app"
            : folder.Equals(Normalize(InstallationOwnership.ManagerDirectory), StringComparison.OrdinalIgnoreCase) ? "manager" : "unknown";
        if (location == "unknown" || installed && location != "app") throw new IOException("installer.metadata_uninstall_location");
        string stem = Path.GetFileNameWithoutExtension(image);
        if (!UninstallerName(Path.GetFileName(image)) || !snapshot.UninstallFiles.Any(file => file.Location == location && file.Name.Equals(stem + ".exe", StringComparison.OrdinalIgnoreCase))
            || !snapshot.UninstallFiles.Any(file => file.Location == location && file.Name.Equals(stem + ".dat", StringComparison.OrdinalIgnoreCase))) throw new IOException("installer.metadata_uninstall_group");
        if (!installed && snapshot.UninstallFiles.Any(file => file.Location != location)) throw new IOException("installer.metadata_unknown_destination");
    }
    private static bool TrustedShortcut(string path, string root)
    {
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            dynamic link = shortcut = ((dynamic)shell!).CreateShortcut(path);
            string target = Path.GetFullPath((string)link.TargetPath);
            string parent = Normalize(Path.GetDirectoryName(target)!);
            return (string)link.Arguments == "" && (target.Equals(Path.Combine(root, "NexusPipeline.exe"), StringComparison.OrdinalIgnoreCase)
                || UninstallerName(Path.GetFileName(target)) && (parent.Equals(Normalize(root), StringComparison.OrdinalIgnoreCase)
                    || parent.Equals(Normalize(InstallationOwnership.ManagerDirectory), StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }
    private static InstallerMetadataSnapshot Capture(string root)
    {
        var uninstaller = new List<InstallerMetadataFile>();
        var shortcuts = new List<InstallerMetadataFile>();
        foreach (string location in new[] { "manager", "app" })
        {
            string folder = location == "app" ? root : InstallationOwnership.ManagerDirectory;
            InstallationOwnership.RequireLinkFree(folder);
            if (!Directory.Exists(folder)) continue;
            foreach (string path in Directory.EnumerateFiles(folder, "unins*"))
            {
                if (!UninstallerName(Path.GetFileName(path))) throw new IOException("installer.metadata_filename");
                uninstaller.Add(new(Path.GetFileName(path), Hash(path)!, location));
            }
        }
        if (uninstaller.Count > 16) throw new IOException("installer.metadata_uninstaller_limit");
        foreach (string location in new[] { "programs", "group", "legacy-group", "desktop" })
        {
            string folder = Path.GetDirectoryName(FilePath(root, new("placeholder.lnk", "", location)))!;
            if (!Directory.Exists(folder)) continue;
            foreach (string path in Directory.EnumerateFiles(folder, "NexusPipeline*.lnk"))
                if (TrustedShortcut(path, root)) shortcuts.Add(new(Path.GetFileName(path), Hash(path)!, location));
        }
        return new(LegacyInstallerMetadataCheckpoint.CaptureRegistry(InstallationOwnership.CurrentUninstallRegistryPath),
            LegacyInstallerMetadataCheckpoint.CaptureRegistry(InstallationOwnership.CurrentRegistryPath),
            uninstaller.OrderBy(Key, StringComparer.Ordinal).ToArray(),
            Hash(Path.Combine(InstallationOwnership.ManagerDirectory, "identity.protected")),
            Hash(Path.Combine(InstallationOwnership.ManagerDirectory, "nexus-installer-helper.exe")), shortcuts.OrderBy(Key, StringComparer.Ordinal).ToArray());
    }
    internal static void Begin(string root, string transactionId, string version, string imageHash, bool upgrade)
    {
        LegacyInstallerMetadataCheckpoint.ValidateArguments(root, transactionId, version, imageHash);
        root = Normalize(root);
        InstallationOwnership.RequireLinkFree(DirectoryPath);
        if (Directory.Exists(DirectoryPath) || File.Exists(DirectoryPath)) throw new IOException("installer.metadata_checkpoint_pending");
        if (!upgrade) InstallationOwnership.ReleaseAbandonedEmptyRegistration(root);
        var identity = InstallationOwnership.Read(root);
        if (identity is null && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("installer.metadata_unknown_destination");
        var before = Capture(root);
        if (identity is null && (before.Uninstall.Exists || before.Ownership.Exists || Files(before).Any() || before.IdentityHash is not null || before.HelperHash is not null)) throw new IOException("installer.metadata_unknown_preexisting");
        if (upgrade != (identity?.State == "active")) throw new IOException("installer.metadata_mode_identity");
        ValidateGroup(root, before, false);
        foreach (string name in new[] { "NexusPipeline v" + version + ".lnk", "NexusPipeline Uninstall.lnk" })
        {
            string destination = FilePath(root, new(name, "", "group"));
            if (File.Exists(destination) && !TrustedShortcut(destination, root)) throw new IOException("installer.metadata_shortcut_conflict");
        }
        Directory.CreateDirectory(BackupPath);
        foreach (var file in Files(before))
        {
            string backup = Path.Combine(BackupPath, Key(file).Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(FilePath(root, file), backup, false);
            if (Hash(backup) != file.Sha256) throw new IOException("installer.metadata_backup_changed");
        }
        string? sourceImage = Hash(Path.Combine(root, "NexusPipeline.exe"));
        if (upgrade && sourceImage is null) throw new IOException("installer.metadata_source_invalid");
        LegacyInstallerMetadataCheckpoint.Write(new(2, transactionId, root, version, imageHash, upgrade, identity?.Version, sourceImage, before, null, "Prepared", DateTimeOffset.UtcNow));
    }
    private static InstallerMetadataState Read(string root, string transactionId)
    {
        var state = LegacyInstallerMetadataCheckpoint.ReadState();
        if (state.SchemaVersion != 2 || state.Root != Normalize(root) || state.TransactionId != transactionId) throw new IOException("installer.metadata_identity");
        return state;
    }
    internal static void Preposition(string root, string transactionId)
    {
        var state = Read(root, transactionId);
        if (state.Status != "Prepared" || !Equal(Capture(root), state.Before)) throw new IOException("installer.metadata_preposition_conflict");
        var copied = state.Before.UninstallFiles.Where(file => file.Location == "manager").Select(file => file with { Location = "app" }).ToArray();
        var expected = state.Before with { UninstallFiles = state.Before.UninstallFiles.Concat(copied).OrderBy(Key, StringComparer.Ordinal).ToArray() };
        state = state with { Status = "Prepositioning", Prepositioned = expected };
        LegacyInstallerMetadataCheckpoint.Write(state);
        foreach (var file in copied)
        {
            string source = FilePath(root, file with { Location = "manager" }), destination = FilePath(root, file);
            if (File.Exists(destination)) throw new IOException("installer.metadata_unknown_destination");
            File.Copy(source, destination, false);
            if (Hash(destination) != file.Sha256) throw new IOException("installer.metadata_preposition_hash");
        }
        if (!Equal(Capture(root), expected)) throw new IOException("installer.metadata_preposition_conflict");
        LegacyInstallerMetadataCheckpoint.Write(state with { Status = "Prepositioned" });
    }
    internal static void Observe(string root, string transactionId)
    {
        var state = Read(root, transactionId);
        if (state.Status != "Prepositioned") throw new IOException("installer.metadata_observe_phase");
        var after = Capture(root);
        ValidateGroup(root, after, true);
        if (after.Uninstall.Values.SingleOrDefault(value => value.Name == "DisplayVersion")?.Text != state.TargetVersion) throw new IOException("installer.metadata_inno_registration_missing");
        LegacyInstallerMetadataCheckpoint.Write(state with { After = after, Status = "InnoWritten" });
    }
    private static void Restore(InstallerMetadataState state, InstallerMetadataSnapshot after)
    {
        var before = Files(state.Before).ToDictionary(Key);
        var post = Files(after).ToDictionary(Key);
        var pre = Files(state.Prepositioned ?? state.Before).ToDictionary(Key);
        foreach (string key in before.Keys.Union(post.Keys).Union(pre.Keys))
        {
            var file = before.GetValueOrDefault(key) ?? post.GetValueOrDefault(key) ?? pre[key];
            string? current = Hash(FilePath(state.Root, file));
            if (current != before.GetValueOrDefault(key)?.Sha256 && current != post.GetValueOrDefault(key)?.Sha256 && current != pre.GetValueOrDefault(key)?.Sha256) throw new IOException("installer.metadata_file_conflict:" + key);
            if (before.TryGetValue(key, out var original) && Hash(Path.Combine(BackupPath, key.Replace('/', Path.DirectorySeparatorChar))) != original.Sha256) throw new IOException("installer.metadata_backup_corrupt:" + key);
        }
        LegacyInstallerMetadataCheckpoint.RestoreRegistry(InstallationOwnership.CurrentUninstallRegistryPath, state.Before.Uninstall, after.Uninstall);
        foreach (string key in before.Keys.Union(post.Keys).Union(pre.Keys))
        {
            var file = before.GetValueOrDefault(key) ?? post.GetValueOrDefault(key) ?? pre[key];
            string target = FilePath(state.Root, file);
            if (Hash(target) == before.GetValueOrDefault(key)?.Sha256) continue;
            if (!before.ContainsKey(key)) { File.Delete(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temporary = target + ".restore-" + Guid.NewGuid().ToString("N");
            File.Copy(Path.Combine(BackupPath, key.Replace('/', Path.DirectorySeparatorChar)), temporary, false);
            File.Move(temporary, target, true);
        }
        var currentSnapshot = Capture(state.Root);
        if (!Equal(currentSnapshot.Uninstall, state.Before.Uninstall) || !Equal(currentSnapshot.UninstallFiles, state.Before.UninstallFiles) || !Equal(currentSnapshot.Shortcuts, state.Before.Shortcuts)) throw new IOException("installer.metadata_restore_incomplete");
    }
    private static void CommitCleanup(InstallerMetadataState state)
    {
        foreach (var file in state.Before.UninstallFiles.Where(file => file.Location == "manager"))
        {
            string path = FilePath(state.Root, file);
            if (!File.Exists(path)) continue;
            if (Hash(path) != file.Sha256) throw new IOException("installer.metadata_cleanup_conflict");
            File.Delete(path);
        }
        foreach (var file in state.Before.Shortcuts ?? [])
        {
            bool retained = file.Location == "group" && (file.Name == "NexusPipeline v" + state.TargetVersion + ".lnk" || file.Name == "NexusPipeline Uninstall.lnk")
                || file.Location == "desktop" && file.Name == "NexusPipeline v" + state.TargetVersion + ".lnk";
            if (retained) continue;
            string path = FilePath(state.Root, file);
            if (!File.Exists(path)) continue;
            if (Hash(path) != file.Sha256) throw new IOException("installer.metadata_cleanup_conflict");
            File.Delete(path);
        }
    }
    internal static string Resolve(string root, string transactionId, bool launched, int childExit, bool registration)
    {
        var state = Read(root, transactionId);
        if (state.Status is not ("InnoWritten" or "CommittedCleanupPending") || state.After is null) throw new IOException("installer.metadata_not_observed");
        string outcome = LegacyInstallerMetadataCheckpoint.Classify(state, launched, childExit, registration);
        if (outcome == "Unknown" && state.Upgrade && launched && childExit != 0 && !registration)
        {
            var pending = UpdateTask.Read(Path.Combine(state.Root, ".nxp-update", "task.json"));
            var owner = InstallationOwnership.Read(state.Root, requireActive: true);
            if (pending is not null && pending.Phase == UpdatePhase.ApplyRequested && pending.PackageSource == "installer"
                && pending.TransactionId == state.TransactionId && pending.Version == state.TargetVersion
                && pending.TargetImageHash == state.TargetImageHash && owner is not null && owner.Version == state.SourceVersion
                && owner.PayloadFiles.All(file => Hash(Path.Combine(state.Root, file.Path.Replace('/', Path.DirectorySeparatorChar))) == file.Sha256)
                && Hash(Path.Combine(state.Root, "NexusPipeline.exe")) == state.SourceImageHash
                && UpdateApply.HasFailedBeforeBackup(pending, state.Root))
            {
                UpdateCleanup.Abort(pending, state.Root, UpdateApply.ObserveWorker);
                outcome = LegacyInstallerMetadataCheckpoint.Classify(state, launched, childExit, registration);
            }
        }
        if (outcome == "Committed")
        {
            var actual = Capture(root);
            if (!Equal(actual.Uninstall, state.After.Uninstall) || !Equal(actual.UninstallFiles.Where(file => file.Location == "app").ToArray(), state.After.UninstallFiles.Where(file => file.Location == "app").ToArray())) throw new IOException("installer.metadata_committed_conflict");
            LegacyInstallerMetadataCheckpoint.Write(state with { Status = "CommittedCleanupPending" });
            CommitCleanup(state);
        }
        else if (outcome == "Restored") Restore(state, state.After);
        else throw new IOException("installer.metadata_phase_unknown: checkpoint retained");
        LegacyInstallerMetadataCheckpoint.Archive(state with { Status = outcome });
        return outcome;
    }
    internal static string RecoverPending(string root)
    {
        if (!Directory.Exists(DirectoryPath)) return "None";
        var state = LegacyInstallerMetadataCheckpoint.ReadState();
        if (state.SchemaVersion == 1) return LegacyInstallerMetadataCheckpoint.RecoverPending(root);
        if (state.Root != Normalize(root)) throw new IOException("installer.metadata_other_root");
        if (state.Status is "Prepared" or "Prepositioning" or "Prepositioned")
        {
            Restore(state, state.Prepositioned ?? state.Before);
            LegacyInstallerMetadataCheckpoint.Archive(state with { Status = "Aborted" });
            return "Aborted";
        }
        if (state.Status is "InnoWritten" or "CommittedCleanupPending") return Resolve(root, state.TransactionId, true, 1, !state.Upgrade);
        throw new IOException("installer.metadata_recovery_phase_unknown: checkpoint retained");
    }
}
