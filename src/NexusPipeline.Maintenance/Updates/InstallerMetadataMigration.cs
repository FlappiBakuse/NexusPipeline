using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.Json;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Updates;

internal static class InstallerMetadataCheckpoint
{
    internal static void StageTargetUninstaller(string root, string transaction, string source, string sha256)
    {
        var state = Read(root, transaction);
        if (state.Status != "InnoWritten" || state.After is null || !UpdateInventory.Hash(sha256)) throw new IOException("installer.tool_stage_phase");
        InstallationOwnership.RequireLinkFree(source);
        if (UpdateApply.ImageHash(source) != sha256) throw new IOException("installer.tool_stage_hash");
        var tool = NexusPipeline.Maintenance.ToolBuildIdentity.Read(source, "uninstaller");
        string staging = Path.Combine(root, ".nxp-update", "staging", state.TargetVersion);
        var target = ApplicationPayload.Validate(staging, state.TargetVersion);
        if (tool.ProductVersion != state.TargetVersion || tool.BuildId != target.BuildId) throw new IOException("installer.tool_stage_identity");
        string destination = FilePath(root, new("target-uninstaller.exe", sha256, "checkpoint"));
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("installer.tool_stage_conflict");
        var files = (state.After.InstallerFiles ?? []).Where(file => !(file.Location == "app" && file.Name == InstallationOwnership.UninstallEntry)).ToList();
        files.Add(new(InstallationOwnership.UninstallEntry, sha256, "app"));
        files.Add(new("target-uninstaller.exe", sha256, "checkpoint"));
        LegacyInstallerMetadataCheckpoint.Write(state with { After = state.After with { InstallerFiles = files.OrderBy(Key, StringComparer.Ordinal).ToArray() } });
        File.Copy(source, destination, false);
        if (Hash(destination) != sha256) throw new IOException("installer.tool_stage_changed");
    }

    internal static void PromoteTargetUninstaller(string root, string version)
    {
        var state = LegacyInstallerMetadataCheckpoint.ReadState();
        if (state.Root != Normalize(root) || state.Status != "InnoWritten" || state.TargetVersion != version || state.After is null) throw new IOException("installer.tool_checkpoint_missing");
        var source = state.After.InstallerFiles?.SingleOrDefault(file => file.Location == "checkpoint" && file.Name == "target-uninstaller.exe") ?? throw new IOException("installer.tool_stage_missing");
        string path = FilePath(root, source);
        if (Hash(path) != source.Sha256) throw new IOException("installer.tool_stage_changed");
        string destination = Path.Combine(root, InstallationOwnership.UninstallEntry);
        string? previous = state.Before.InstallerFiles?.SingleOrDefault(file => file.Location == "app" && file.Name == InstallationOwnership.UninstallEntry)?.Sha256;
        string? actual = Hash(destination);
        if (actual is not null && actual != previous && actual != source.Sha256) throw new IOException("installer.unknown_uninstall_entry_preserved");
        string temporary = Path.Combine(DirectoryPath, "uninstaller-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.Copy(path, temporary, false);
        if (Hash(temporary) != source.Sha256) throw new IOException("installer.tool_copy_changed");
        File.Move(temporary, destination, true);
    }

    internal static void StageInstallerTools(InstallerInstanceIdentity identity, string seal)
    {
        var state = LegacyInstallerMetadataCheckpoint.ReadState();
        if (state.Root != Normalize(identity.AppRoot) || state.Status != "InnoWritten" || state.After is null
            || state.TargetVersion != identity.Version) throw new IOException("installer.tools_checkpoint_required");
        var current = Capture(identity.AppRoot);
        if (!Equal(current.Uninstall, state.After.Uninstall) || !Equal(current.UninstallFiles, state.After.UninstallFiles)
            || !Equal(current.InstallerFiles, state.After.InstallerFiles) || !Equal(current.Ownership, state.After.Ownership)) throw new IOException("installer.native_metadata_changed");
        var files = (current.InstallerFiles ?? []).Where(file => file.Name is not ("identity.protected" or "nexus-uninstall-worker.exe" or "nexus-installer-helper.exe")).ToList();
        byte[] identityBytes = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(seal)];
        files.Add(new("identity.protected", Convert.ToHexStringLower(SHA256.HashData(identityBytes)), "manager"));
        files.Add(new(InstallationOwnership.UninstallWorker, identity.InstallerTools!.Worker.Sha256, "manager"));
        var replacements = new Dictionary<string, string> { ["InstanceId"] = identity.InstanceId,
            ["IdentityDigest"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seal))),
            ["DataRoot"] = identity.AppRoot, ["AppDir"] = identity.AppRoot };
        var values = current.Ownership.Values.Where(value => !replacements.ContainsKey(value.Name))
            .Concat(replacements.Select(pair => new InstallerRegistryValue(pair.Key, RegistryValueKind.String, pair.Value, null, null, null)))
            .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var expected = current with { Ownership = new(true, values), InstallerFiles = files.OrderBy(Key, StringComparer.Ordinal).ToArray(),
            IdentityHash = files.Single(file => file.Name == "identity.protected").Sha256,
            HelperHash = identity.InstallerTools.Worker.Sha256 };
        LegacyInstallerMetadataCheckpoint.Write(state with { After = expected });
    }

    private static void RestoreOwnership(InstallerMetadataState state, InstallerMetadataSnapshot after)
    {
        var current = LegacyInstallerMetadataCheckpoint.CaptureRegistry(InstallationOwnership.CurrentRegistryPath);
        var beforeValues = state.Before.Ownership.Values.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        var afterValues = after.Ownership.Values.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var value in current.Values)
            if (!Equal(value, beforeValues.GetValueOrDefault(value.Name)) && !Equal(value, afterValues.GetValueOrDefault(value.Name))) throw new IOException("installer.ownership_registry_conflict");
        foreach (var name in beforeValues.Keys.Intersect(afterValues.Keys))
            if (!current.Values.Any(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new IOException("installer.ownership_registry_missing");
        LegacyInstallerMetadataCheckpoint.RestoreRegistry(InstallationOwnership.CurrentRegistryPath, state.Before.Ownership, current);
    }

    internal static void ValidateTransition(string root, string transaction, string version, string imageHash)
    {
        var state = Read(root, transaction);
        if (!state.Upgrade || state.Status != "InnoWritten" || state.After is null || state.TargetVersion != version
            || state.TargetImageHash != imageHash || state.SourceImageHash != Hash(Path.Combine(root, "NexusPipeline.exe")))
            throw new IOException("installer.transition_checkpoint_mismatch");
        var identity = InstallationOwnership.Read(root, requireActive: true) ?? throw new IOException("installer.instance_unowned");
        if (identity.Version != state.SourceVersion) throw new IOException("installer.transition_source_changed");
        _ = InstallationOwnership.SnapshotBytesForUpdate(root);
    }

    internal static string WorkDirectory => DirectoryPath;
    private static string DirectoryPath => Path.Combine(InstallationOwnership.ManagerDirectory, "metadata-checkpoint");
    private static string BackupPath => Path.Combine(DirectoryPath, "uninstaller-backup");
    private static string Normalize(string root) => Path.GetFullPath(root).TrimEnd('\\');
    private static string Key(InstallerMetadataFile file) => (file.Location ?? "manager") + "/" + file.Name;
    private static IEnumerable<InstallerMetadataFile> Files(InstallerMetadataSnapshot snapshot) => snapshot.UninstallFiles.Concat(snapshot.Shortcuts ?? []).Concat(snapshot.InstallerFiles ?? []);
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
            "checkpoint" when file.Name == "target-uninstaller.exe" => DirectoryPath,
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
                || target.Equals(Path.Combine(root, InstallationOwnership.UninstallEntry), StringComparison.OrdinalIgnoreCase)
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
        var installerFiles = new List<InstallerMetadataFile>();
        foreach (var item in new[] { ("app", InstallationOwnership.UninstallEntry), ("manager", InstallationOwnership.UninstallWorker), ("manager", "nexus-installer-helper.exe"), ("manager", "identity.protected"), ("checkpoint", "target-uninstaller.exe") })
        {
            string path = FilePath(root, new(item.Item2, "", item.Item1));
            if (Hash(path) is { } hash) installerFiles.Add(new(item.Item2, hash, item.Item1));
        }
        return new(LegacyInstallerMetadataCheckpoint.CaptureRegistry(InstallationOwnership.CurrentUninstallRegistryPath),
            LegacyInstallerMetadataCheckpoint.CaptureRegistry(InstallationOwnership.CurrentRegistryPath),
            uninstaller.OrderBy(Key, StringComparer.Ordinal).ToArray(),
            Hash(Path.Combine(InstallationOwnership.ManagerDirectory, "identity.protected")),
            Hash(Path.Combine(InstallationOwnership.ManagerDirectory, InstallationOwnership.UninstallWorker)) ?? Hash(Path.Combine(InstallationOwnership.ManagerDirectory, "nexus-installer-helper.exe")), shortcuts.OrderBy(Key, StringComparer.Ordinal).ToArray(), installerFiles.OrderBy(Key, StringComparer.Ordinal).ToArray());
    }
    internal static void Begin(string root, string transactionId, string version, string imageHash, bool upgrade, NexusPipeline.Platform.Processes.ProcessIdentity? setupOwner = null)
    {
        LegacyInstallerMetadataCheckpoint.ValidateArguments(root, transactionId, version, imageHash);
        root = Normalize(root);
        InstallationOwnership.RequireLinkFree(DirectoryPath);
        if (Directory.Exists(DirectoryPath) || File.Exists(DirectoryPath)) throw new IOException("installer.metadata_checkpoint_pending");
        if (!upgrade) InstallationOwnership.ReleaseAbandonedEmptyRegistration(root);
        var identity = InstallationOwnership.Read(root);
        if (identity is null && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("installer.metadata_unknown_destination");
        if (identity is { SchemaVersion: 1 } && (File.Exists(Path.Combine(root, InstallationOwnership.UninstallEntry))
            || Directory.Exists(Path.Combine(root, InstallationOwnership.UninstallEntry)))) throw new IOException("installer.unknown_uninstall_entry_preserved");
        if (identity is { SchemaVersion: 2 })
        {
            _ = InstallationOwnership.InspectInstallerTools(identity);
            if (identity.State == "active" && Hash(identity.InstallerTools!.NativeEngine.Path) != identity.InstallerTools.NativeEngine.Sha256) throw new IOException("installer.native_engine_changed");
        }
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
        LegacyInstallerMetadataCheckpoint.Write(new(3, transactionId, root, version, imageHash, upgrade, identity?.Version, sourceImage, before, null, "Prepared", DateTimeOffset.UtcNow, SetupOwner: setupOwner));
    }
    private static InstallerMetadataState Read(string root, string transactionId)
    {
        var state = LegacyInstallerMetadataCheckpoint.ReadState();
        if (state.SchemaVersion is not (2 or 3) || state.Root != Normalize(root) || state.TransactionId != transactionId) throw new IOException("installer.metadata_identity");
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
        RestoreOwnership(state, after);
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
        if (!Equal(currentSnapshot.Uninstall, state.Before.Uninstall) || !Equal(currentSnapshot.UninstallFiles, state.Before.UninstallFiles) || !Equal(currentSnapshot.Shortcuts, state.Before.Shortcuts) || !Equal(currentSnapshot.InstallerFiles, state.Before.InstallerFiles) || !Equal(currentSnapshot.Ownership, state.Before.Ownership)) throw new IOException("installer.metadata_restore_incomplete");
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
            if (retained || state.Before.InstallerFiles?.Contains(file) == true) continue;
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
        if (outcome == "Unknown" && childExit != 0 && state.Upgrade
            && Hash(Path.Combine(root, "NexusPipeline.exe")) == state.SourceImageHash)
        {
            var pending = UpdateTask.Read(Path.Combine(root, ".nxp-update", "task.json"));
            if (pending is null && !Directory.Exists(Path.Combine(root, ".nxp-backup"))
                || pending is { Phase: UpdatePhase.RollbackConfirmed } && UpdateApply.ObserveWorker(pending.WorkerIdentity) == false)
            {
                Restore(state, state.After);
                LegacyInstallerMetadataCheckpoint.Archive(state with { Status = "Restored" });
                return "Restored";
            }
        }
        if (outcome == "Unknown" && childExit != 0 && registration && !state.Upgrade)
        {
            Restore(state, state.After);
            LegacyInstallerMetadataCheckpoint.Archive(state with { Status = "Restored" });
            return "Restored";
        }
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
    internal static void RequireStartupAllowed(string root)
    {
        if (!Directory.Exists(DirectoryPath)) return;
        var state = LegacyInstallerMetadataCheckpoint.ReadState();
        if (state.Root != Normalize(root)) return;
        if (state.SetupOwner is not { } owner || UpdateApply.ObserveWorker(owner) == false) return;
        var task = UpdateTask.Read(Path.Combine(root, ".nxp-update", "task.json"));
        if (task is { SetupTransition: true, Phase: UpdatePhase.AwaitingStartup }
            && task.TransactionId == state.TransactionId && task.Version == state.TargetVersion
            && task.TargetImageHash == state.TargetImageHash
            && Hash(Path.Combine(root, "NexusPipeline.exe")) == state.TargetImageHash
            && task.WorkerIdentity is not null && UpdateApply.ObserveWorker(task.WorkerIdentity) == true) return;
        throw new IOException("installer.setup_in_progress");
    }

    internal static string RecoverPending(string root)
    {
        if (!Directory.Exists(DirectoryPath)) return "None";
        var state = LegacyInstallerMetadataCheckpoint.ReadState();
        if (state.SetupOwner is { } owner && UpdateApply.ObserveWorker(owner) != false) throw new IOException("installer.setup_in_progress");
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
