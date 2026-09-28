using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Versioning;

namespace NexusPipeline.Modules.Updates;

internal sealed record InstallerRegistryValue(string Name, RegistryValueKind Kind, string? Text,
    string[]? Lines, string? Binary, long? Number);
internal sealed record InstallerRegistrySnapshot(bool Exists, InstallerRegistryValue[] Values);
internal sealed record InstallerMetadataFile(string Name, string Sha256);
internal sealed record InstallerMetadataSnapshot(InstallerRegistrySnapshot Uninstall,
    InstallerRegistrySnapshot Ownership, InstallerMetadataFile[] UninstallFiles,
    string? IdentityHash, string? HelperHash);
internal sealed record InstallerMetadataState(int SchemaVersion, string TransactionId, string Root,
    string TargetVersion, string TargetImageHash, bool Upgrade, string? SourceVersion, string? SourceImageHash,
    InstallerMetadataSnapshot Before, InstallerMetadataSnapshot? After, string Status,
    DateTimeOffset CreatedAtUtc);

/// <summary>Owns the narrow ARP and uninstaller checkpoint taken before Inno changes either one.</summary>
internal static class InstallerMetadataCheckpoint
{
    private const string CheckpointName = "metadata-checkpoint";
    private static string DirectoryPath => Path.Combine(InstallationOwnership.ManagerDirectory, CheckpointName);
    private static string StatePath => Path.Combine(DirectoryPath, "state.protected");
    private static string BackupPath => Path.Combine(DirectoryPath, "uninstaller-backup");

    internal static void Begin(string root, string transactionId, string version, string imageHash, bool upgrade)
    {
        ValidateArguments(root, transactionId, version, imageHash);
        root = Normalize(root);
        InstallationOwnership.RequireLinkFree(root);
        InstallationOwnership.RequireLinkFree(InstallationOwnership.ManagerDirectory);
        InstallationOwnership.RequireLinkFree(DirectoryPath);
        if (Directory.Exists(DirectoryPath) || File.Exists(DirectoryPath))
            throw new IOException("installer.metadata_checkpoint_pending");
        var identity = InstallationOwnership.Read(root);
        var before = Capture();
        if (identity is null && (before.Uninstall.Exists || before.Ownership.Exists
            || before.UninstallFiles.Length != 0 || before.IdentityHash is not null || before.HelperHash is not null))
            throw new IOException("installer.metadata_unknown_preexisting");
        string? sourceImage = File.Exists(Path.Combine(root, "nexus-pipeline.exe"))
            ? UpdateApply.ImageHash(Path.Combine(root, "nexus-pipeline.exe")) : null;
        if (identity is not null && (identity.State is not ("active" or "retained-data")
            || identity.State == "active" && sourceImage is null))
            throw new IOException("installer.metadata_source_invalid");
        if (upgrade && identity?.State != "active") throw new IOException("installer.metadata_upgrade_identity");
        if (!upgrade && identity?.State == "active") throw new IOException("installer.metadata_registration_identity");
        Directory.CreateDirectory(DirectoryPath);
        Directory.CreateDirectory(BackupPath);
        foreach (var file in before.UninstallFiles)
        {
            string source = Path.Combine(InstallationOwnership.ManagerDirectory, file.Name);
            string backup = Path.Combine(BackupPath, file.Name);
            InstallationOwnership.RequireLinkFree(source);
            File.Copy(source, backup, false);
            if (UpdateApply.ImageHash(backup) != file.Sha256)
                throw new IOException("installer.metadata_backup_changed");
        }
        Write(new(1, transactionId, root, version, imageHash, upgrade, identity?.Version, sourceImage,
            before, null, "Prepared", DateTimeOffset.UtcNow));
    }

    internal static string RecoverPending(string root)
    {
        if (!Directory.Exists(DirectoryPath)) return "None";
        var state = ReadState();
        if (state.Root != Normalize(root)) throw new IOException("installer.metadata_other_root");
        if (state.Status == "Prepared")
        {
            var current = Capture();
            if (RegistryEqual(current.Uninstall, state.Before.Uninstall)
                && FilesEqual(current.UninstallFiles, state.Before.UninstallFiles)
                && RegistryEqual(current.Ownership, state.Before.Ownership)
                && current.IdentityHash == state.Before.IdentityHash
                && current.HelperHash == state.Before.HelperHash)
            {
                Archive(state with { Status = "Aborted" });
                return "Aborted";
            }
            ObserveState(state, false);
            state = ReadState();
        }
        if (state.Status != "InnoWritten")
            throw new IOException("installer.metadata_recovery_phase_unknown: checkpoint retained");
        return Resolve(root, state.TransactionId, true, 1, !state.Upgrade);
    }

    internal static void Observe(string root, string transactionId)
    {
        var state = Read(root, transactionId);
        ObserveState(state, true);
    }

    private static void ObserveState(InstallerMetadataState state, bool requireTargetRegistration)
    {
        if (state.Status != "Prepared" || state.After is not null)
            throw new IOException("installer.metadata_observe_phase");
        var after = Capture();
        if (requireTargetRegistration && (!after.Uninstall.Exists || !string.Equals(
            ReadString(after.Uninstall, "DisplayVersion"), state.TargetVersion, StringComparison.Ordinal)))
            throw new IOException("installer.metadata_inno_registration_missing");
        Write(state with { After = after, Status = "InnoWritten" });
    }

    internal static string Resolve(string root, string transactionId, bool launched, int childExit, bool registration)
    {
        var state = Read(root, transactionId);
        if (state.Status != "InnoWritten" || state.After is null)
            throw new IOException("installer.metadata_not_observed");
        string outcome = Classify(state, launched, childExit, registration);
        if (outcome == "Committed")
        {
            if (!RegistryEqual(CaptureRegistry(InstallationOwnership.CurrentUninstallRegistryPath), state.After.Uninstall)
                || !FilesEqual(CaptureFiles(), state.After.UninstallFiles))
                throw new IOException("installer.metadata_committed_conflict");
        }
        else if (outcome == "Restored")
        {
            PreflightRestore(state.Before, state.After);
            RestoreRegistry(InstallationOwnership.CurrentUninstallRegistryPath,
                state.Before.Uninstall, state.After.Uninstall);
            RestoreFiles(state.Before.UninstallFiles, state.After.UninstallFiles);
            if (!RegistryEqual(CaptureRegistry(InstallationOwnership.CurrentUninstallRegistryPath), state.Before.Uninstall)
                || !FilesEqual(CaptureFiles(), state.Before.UninstallFiles))
                throw new IOException("installer.metadata_restore_incomplete");
        }
        else if (registration && childExit != 0)
        {
            PreflightRestore(state.Before, state.After);
            RestoreRegistry(InstallationOwnership.CurrentUninstallRegistryPath,
                state.Before.Uninstall, state.After.Uninstall);
            RestoreFiles(state.Before.UninstallFiles, state.After.UninstallFiles);
            Write(state with { Status = "OwnershipPending" });
            throw new IOException("installer.metadata_ownership_pending: ARP and uninstaller restored; identity retained for diagnosis");
        }
        else
            throw new IOException("installer.metadata_phase_unknown: checkpoint retained");
        Archive(state with { Status = outcome });
        return outcome;
    }

    private static void Archive(InstallerMetadataState state)
    {
        Write(state);
        string archived = Path.Combine(InstallationOwnership.ManagerDirectory,
            CheckpointName + "-" + state.TransactionId + "-" + state.Status.ToLowerInvariant());
        if (Directory.Exists(archived) || File.Exists(archived))
            throw new IOException("installer.metadata_archive_conflict");
        Directory.Move(DirectoryPath, archived);
    }

    private static string Classify(InstallerMetadataState state, bool launched, int childExit, bool registration)
    {
        InstallerInstanceIdentity? identity;
        try { identity = InstallationOwnership.Read(state.Root); }
        catch { return "Unknown"; }
        string image = Path.Combine(state.Root, "nexus-pipeline.exe");
        string? currentHash = File.Exists(image) ? UpdateApply.ImageHash(image) : null;
        bool target = identity?.State == "active" && identity.Version == state.TargetVersion
            && currentHash == state.TargetImageHash;
        bool source = identity?.Version == state.SourceVersion && currentHash == state.SourceImageHash;
        if (state.SourceVersion is null)
            source = identity is null && state.SourceImageHash is null;
        if (registration != !state.Upgrade) return "Unknown";
        if (registration)
            return childExit == 0 && target ? "Committed"
                : identity is null || identity.State == "retained-data" && identity.Version == state.SourceVersion
                    ? "Restored" : "Unknown";
        string resultPath = Path.Combine(state.Root, ".nxp", "state", "updates",
            state.TransactionId + ".result.json");
        string taskPath = Path.Combine(state.Root, ".nxp-update", "task.json");
        JsonElement? receipt = ReadDocument(resultPath);
        JsonElement? task = ReadDocument(taskPath);
        if (receipt is { } result && (!Matches(result, "TransactionId", state.TransactionId)
            || !Matches(result, "Version", state.TargetVersion)))
            return "Unknown";
        if (task is { } journal && !Matches(journal, "TransactionId", state.TransactionId))
            return "Unknown";
        if (!launched)
            return receipt is null && task is null && source ? "Restored" : "Unknown";
        bool committedReceipt = receipt is { } done
            && done.TryGetProperty("Succeeded", out var ok) && ok.ValueKind == JsonValueKind.True
            && Matches(done, "Code", "committed");
        bool committedPhase = task is { } pending && Matches(pending, "Phase", UpdatePhase.Committed);
        string versionPath = Path.Combine(state.Root, ".nxp-version");
        bool committedVersion = File.Exists(versionPath)
            && File.ReadAllText(versionPath).Trim() == state.TargetVersion;
        if (target && (committedReceipt || (receipt is { } failed
            && Matches(failed, "Code", "committed_cleanup_pending") && (committedPhase || committedVersion))))
            return "Committed";
        if (childExit == 0) return "Unknown";
        bool rollbackConfirmed = task is { } rolled && Matches(rolled, "Phase", UpdatePhase.RollbackConfirmed);
        bool noWorkerTransaction = task is null
            && !Directory.Exists(Path.Combine(state.Root, ".nxp-backup"))
            && (receipt is null || receipt is { } failedResult
                && failedResult.TryGetProperty("Succeeded", out var success)
                && success.ValueKind == JsonValueKind.False);
        if (source && (rollbackConfirmed || noWorkerTransaction)) return "Restored";
        return "Unknown";
    }

    private static void PreflightRestore(InstallerMetadataSnapshot before, InstallerMetadataSnapshot after)
    {
        var currentRegistry = CaptureRegistry(InstallationOwnership.CurrentUninstallRegistryPath);
        if (!RegistryEqual(currentRegistry, before.Uninstall)
            && !RegistryEqual(currentRegistry, after.Uninstall))
            throw new IOException("installer.metadata_registry_conflict");
        var original = before.UninstallFiles.ToDictionary(file => file.Name, StringComparer.OrdinalIgnoreCase);
        var installed = after.UninstallFiles.ToDictionary(file => file.Name, StringComparer.OrdinalIgnoreCase);
        foreach (string name in original.Keys.Union(installed.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsUninstallerName(name)) throw new IOException("installer.metadata_filename");
            string path = Path.Combine(InstallationOwnership.ManagerDirectory, name);
            string? current = HashIfExists(path);
            string? oldHash = original.GetValueOrDefault(name)?.Sha256;
            string? newHash = installed.GetValueOrDefault(name)?.Sha256;
            if (current != oldHash && current != newHash)
                throw new IOException("installer.metadata_uninstaller_conflict:" + name);
            if (oldHash is not null)
            {
                string backup = Path.Combine(BackupPath, name);
                if (HashIfExists(backup) != oldHash)
                    throw new IOException("installer.metadata_backup_corrupt:" + name);
            }
        }
    }

    private static JsonElement? ReadDocument(string path)
    {
        InstallationOwnership.RequireLinkFree(path);
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    private static bool Matches(JsonElement element, string name, string value) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String && property.GetString() == value;

    private static InstallerMetadataSnapshot Capture() => new(
        CaptureRegistry(InstallationOwnership.CurrentUninstallRegistryPath),
        CaptureRegistry(InstallationOwnership.CurrentRegistryPath),
        CaptureFiles(), HashIfExists(Path.Combine(InstallationOwnership.ManagerDirectory, "identity.protected")),
        HashIfExists(Path.Combine(InstallationOwnership.ManagerDirectory, "nexus-installer-helper.exe")));

    private static string? HashIfExists(string path)
    {
        InstallationOwnership.RequireLinkFree(path);
        return File.Exists(path) ? UpdateApply.ImageHash(path) : null;
    }

    private static InstallerRegistrySnapshot CaptureRegistry(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, false);
        if (key is null) return new(false, []);
        if (key.GetSubKeyNames().Length != 0) throw new IOException("installer.metadata_registry_subkeys");
        var values = key.GetValueNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                RegistryValueKind kind = key.GetValueKind(name);
                object? raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                return kind switch
                {
                    RegistryValueKind.String or RegistryValueKind.ExpandString =>
                        new InstallerRegistryValue(name, kind, (string)raw!, null, null, null),
                    RegistryValueKind.MultiString =>
                        new InstallerRegistryValue(name, kind, null, (string[])raw!, null, null),
                    RegistryValueKind.Binary or RegistryValueKind.None =>
                        new InstallerRegistryValue(name, kind, null, null, Convert.ToBase64String((byte[])raw!), null),
                    RegistryValueKind.DWord or RegistryValueKind.QWord =>
                        new InstallerRegistryValue(name, kind, null, null, null, Convert.ToInt64(raw)),
                    _ => throw new IOException("installer.metadata_registry_kind"),
                };
            }).ToArray();
        return new(true, values);
    }

    private static InstallerMetadataFile[] CaptureFiles()
    {
        string manager = InstallationOwnership.ManagerDirectory;
        InstallationOwnership.RequireLinkFree(manager);
        if (!Directory.Exists(manager)) return [];
        var files = Directory.EnumerateFiles(manager, "unins*", SearchOption.TopDirectoryOnly)
            .Where(path => IsUninstallerName(Path.GetFileName(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length > 16) throw new IOException("installer.metadata_uninstaller_limit");
        return files.Select(path =>
        {
            InstallationOwnership.RequireLinkFree(path);
            return new InstallerMetadataFile(Path.GetFileName(path), UpdateApply.ImageHash(path));
        }).ToArray();
    }

    private static bool IsUninstallerName(string name) =>
        name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
        && (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".msg", StringComparison.OrdinalIgnoreCase))
        && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');

    private static void RestoreFiles(InstallerMetadataFile[] before, InstallerMetadataFile[] after)
    {
        var old = before.ToDictionary(file => file.Name, StringComparer.OrdinalIgnoreCase);
        var post = after.ToDictionary(file => file.Name, StringComparer.OrdinalIgnoreCase);
        foreach (string name in old.Keys.Union(post.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsUninstallerName(name)) throw new IOException("installer.metadata_filename");
            string target = Path.Combine(InstallationOwnership.ManagerDirectory, name);
            InstallationOwnership.RequireLinkFree(target);
            string? current = HashIfExists(target);
            string? original = old.GetValueOrDefault(name)?.Sha256;
            string? installed = post.GetValueOrDefault(name)?.Sha256;
            if (current == original) continue;
            if (current != installed) throw new IOException("installer.metadata_uninstaller_conflict:" + name);
            if (original is null) { File.Delete(target); continue; }
            string backup = Path.Combine(BackupPath, name);
            InstallationOwnership.RequireLinkFree(backup);
            if (UpdateApply.ImageHash(backup) != original)
                throw new IOException("installer.metadata_backup_corrupt:" + name);
            string temp = target + ".restore-" + Guid.NewGuid().ToString("N");
            File.Copy(backup, temp, false);
            File.Move(temp, target, true);
        }
    }

    private static void RestoreRegistry(string path, InstallerRegistrySnapshot before, InstallerRegistrySnapshot after)
    {
        var current = CaptureRegistry(path);
        if (RegistryEqual(current, before)) return;
        if (!RegistryEqual(current, after)) throw new IOException("installer.metadata_registry_conflict");
        if (!before.Exists)
        {
            Registry.CurrentUser.DeleteSubKey(path, false);
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(path, true)
            ?? throw new IOException("installer.metadata_registry_write");
        var names = new HashSet<string>(before.Values.Select(value => value.Name), StringComparer.OrdinalIgnoreCase);
        foreach (string name in key.GetValueNames())
            if (!names.Contains(name)) key.DeleteValue(name, false);
        foreach (var value in before.Values)
        {
            object data = value.Kind switch
            {
                RegistryValueKind.String or RegistryValueKind.ExpandString => value.Text!,
                RegistryValueKind.MultiString => value.Lines!,
                RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromBase64String(value.Binary!),
                RegistryValueKind.DWord => checked((int)value.Number!.Value),
                RegistryValueKind.QWord => value.Number!.Value,
                _ => throw new IOException("installer.metadata_registry_kind"),
            };
            key.SetValue(value.Name, data, value.Kind);
        }
    }

    private static string? ReadString(InstallerRegistrySnapshot snapshot, string name) =>
        snapshot.Values.SingleOrDefault(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Text;

    private static bool RegistryEqual(InstallerRegistrySnapshot left, InstallerRegistrySnapshot right) =>
        JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
    private static bool FilesEqual(InstallerMetadataFile[] left, InstallerMetadataFile[] right) =>
        JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    private static InstallerMetadataState Read(string root, string transactionId)
    {
        var state = ReadState();
        if (state.TransactionId != transactionId || state.Root != Normalize(root))
            throw new IOException("installer.metadata_identity");
        return state;
    }

    private static InstallerMetadataState ReadState()
    {
        InstallationOwnership.RequireLinkFree(DirectoryPath);
        InstallationOwnership.RequireLinkFree(StatePath);
        string seal = File.ReadAllText(StatePath);
        byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(seal), null,
            DataProtectionScope.CurrentUser);
        var state = JsonSerializer.Deserialize<InstallerMetadataState>(bytes)
            ?? throw new IOException("installer.metadata_empty");
        if (state.SchemaVersion != 1 || !Guid.TryParseExact(state.TransactionId, "N", out _)
            || state.Before is null || state.CreatedAtUtc == default)
            throw new IOException("installer.metadata_identity");
        return state;
    }

    private static void Write(InstallerMetadataState state)
    {
        string json = JsonSerializer.Serialize(state);
        string seal = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null,
            DataProtectionScope.CurrentUser));
        JsonUtil.WriteAtomic(StatePath, seal);
    }

    private static string Normalize(string root) => Path.GetFullPath(root).TrimEnd('\\');
    private static void ValidateArguments(string root, string transactionId, string version, string imageHash)
    {
        if (!Path.IsPathFullyQualified(root) || !Guid.TryParseExact(transactionId, "N", out _)
            || !NexusVersion.TryParse(version, out _) || imageHash.Length != 64
            || imageHash.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidDataException("installer.metadata_arguments");
    }
}
