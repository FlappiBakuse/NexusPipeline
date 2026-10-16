using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Updates;

internal sealed record InstalledPayloadFile(string Path, string Sha256);
internal sealed record InstallerInstanceIdentity(int SchemaVersion, string InstanceId, string AppRoot,
    string WindowsUserId, DateTimeOffset CreatedAtUtc, string State, string Version,
    IReadOnlyList<InstalledPayloadFile> PayloadFiles)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public InstalledPayloadFile[]? AuxiliaryPayloadFiles { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public InstallerTools? InstallerTools { get; init; }
}

/// <summary>Setup instance and retained-data ownership; updates keep this same payload manifest current.</summary>
internal static partial class InstallationOwnership
{
    internal const string RegistryPath = @"Software\NexusPipeline.Generations\" + InstallationGeneration.Id + @"\Installer";
    internal static string CurrentRegistryPath => TestScope() is { } scope ? @"Software\NexusPipeline\Tests\Installer\" + scope.Id : RegistryPath;
    internal static string CurrentUninstallRegistryPath => TestScope() is { } scope
        ? Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_NATIVE") == "1"
            ? @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NexusPipeline.Tests." + scope.Id + "_is1"
            : @"Software\NexusPipeline\Tests\Uninstall\" + scope.Id
        : @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NexusPipeline.PerUser." + InstallationGeneration.Id + "_is1";
    internal static string ManagerDirectory => TestScope() is { } scope ? Path.Combine(scope.Root, "manager")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusPipeline.Generations", InstallationGeneration.Id, "installer");
    private static string RegistrationRecoveryDirectory => TestScope() is { } scope ? Path.Combine(scope.Root, "registration-recovery")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusPipeline.Generations", InstallationGeneration.Id, "registration-recovery");
    private static (string Id, string Root)? TestScope()
    {
#if NEXUS_TEST_HOST
        string? id = Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE");
        string? root = Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT");
        if (id is not null || root is not null)
        {
            if (!Guid.TryParseExact(id, "N", out _) || root is null || !Path.IsPathFullyQualified(root)
                || File.ReadAllText(Path.Combine(root, ".nxp-installer-lab")) != id)
                throw new IOException("installer.test_scope_ownership");
            return (id!, Path.GetFullPath(root));
        }
#endif
        if (Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_SCOPE") is not null
            || Environment.GetEnvironmentVariable("NEXUS_INSTALLER_TEST_ROOT") is not null)
            throw new IOException("installer.test_host_required");
        return null;
    }
    private static string IdentityPath => Path.Combine(ManagerDirectory, "identity.protected");
    private static string CurrentUserId => WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("installer.user_identity");

    internal static void RequireLinkFree(string path)
    {
        for (string? part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("installer.link_path");
    }

    internal static void RequireSeparateRoots(string root, IEnumerable<string> protectedRoots)
    {
        string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        RequireLinkFree(full);
        foreach (string protectedRoot in protectedRoots)
        {
            string other = Path.GetFullPath(protectedRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(full, other, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || other.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("installer.generation_path_overlap");
        }
    }

    private static void RequireGenerationDestination(string root)
    {
        var protectedRoots = new List<string> { ManagerDirectory, RegistrationRecoveryDirectory };
        var scope = TestScope();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        protectedRoots.Add(scope is { } fixture ? Path.Combine(fixture.Root, "old-app")
            : Path.Combine(local, "Programs", "NexusPipeline"));
        string legacyKey = scope is { } registry ? @"Software\NexusPipeline\Tests\LegacyInstaller\" + registry.Id
            : @"Software\NexusPipeline\Installer";
        using var key = Registry.CurrentUser.OpenSubKey(legacyKey, false);
        foreach (string name in new[] { "DataRoot", "AppDir" })
            if (key?.GetValue(name) is string path && !string.IsNullOrWhiteSpace(path)) protectedRoots.Add(path);
        string uninstallKey = scope is { } uninstall ? @"Software\NexusPipeline\Tests\LegacyUninstall\" + uninstall.Id
            : @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NexusPipeline.PerUser_is1";
        using var oldUninstall = Registry.CurrentUser.OpenSubKey(uninstallKey, false);
        if (oldUninstall?.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location))
            protectedRoots.Add(location);
        if (scope is null)
        {
            protectedRoots.Add(Path.Combine(local, "NexusPipeline.Generations", "g01615"));
            using var oldGeneration = Registry.CurrentUser.OpenSubKey(@"Software\NexusPipeline.Generations\g01615\Installer", false);
            foreach (string name in new[] { "DataRoot", "AppDir" })
                if (oldGeneration?.GetValue(name) is string path && !string.IsNullOrWhiteSpace(path)) protectedRoots.Add(path);
            using var oldGenerationUninstall = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\NexusPipeline.PerUser.g01615_is1", false);
            if (oldGenerationUninstall?.GetValue("InstallLocation") is string oldRoot && !string.IsNullOrWhiteSpace(oldRoot)) protectedRoots.Add(oldRoot);
        }
        RequireSeparateRoots(root, protectedRoots);
    }

    internal static string Seal(InstallerInstanceIdentity identity)
    {
        if (identity.AuxiliaryPayloadFiles is not null) throw new InvalidDataException("installer.identity_not_normalized");
        // Legacy Inno readers require their original common payload projection.
        var auxiliary = identity.SchemaVersion == 1 ? identity.PayloadFiles.Where(file => file.Path == UpdateRecipe.UpdateEntry).ToArray() : [];
        var wire = identity with { PayloadFiles = auxiliary.Length == 0 ? identity.PayloadFiles : identity.PayloadFiles.Where(file => file.Path != UpdateRecipe.UpdateEntry).ToArray(),
            AuxiliaryPayloadFiles = auxiliary.Length == 0 ? null : auxiliary };
        return Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(wire)), null, DataProtectionScope.CurrentUser));
    }

    internal static InstallerInstanceIdentity Unseal(string seal, string expectedRoot, string instanceId, string userId)
    {
        var identity = JsonSerializer.Deserialize<InstallerInstanceIdentity>(ProtectedData.Unprotect(
            Convert.FromBase64String(seal), null, DataProtectionScope.CurrentUser)) ?? throw new InvalidDataException("installer.identity_empty");
        if (identity.PayloadFiles is null) throw new InvalidDataException("installer.payload_manifest");
        if (identity.AuxiliaryPayloadFiles is { } auxiliary)
        {
            if (auxiliary.Length != 1 || auxiliary[0]?.Path is not (UpdateRecipe.UpdateEntry or ApplicationPayload.LegacyUpdateEntry))
                throw new InvalidDataException("installer.auxiliary_payload_manifest");
            identity = identity with { PayloadFiles = identity.PayloadFiles.Concat(auxiliary).OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(),
                AuxiliaryPayloadFiles = null };
        }
        if (identity.SchemaVersion is not (1 or 2) || !Guid.TryParseExact(identity.InstanceId, "N", out _)
            || identity.InstanceId != instanceId || identity.WindowsUserId != userId
            || identity.CreatedAtUtc == default || identity.State is not ("active" or "retained-data" or "manual-adoption-pending")
            || !string.Equals(Path.GetFullPath(identity.AppRoot).TrimEnd('\\'), Path.GetFullPath(expectedRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            || identity.PayloadFiles.Count > 20000)
            throw new InvalidDataException("installer.identity_mismatch");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in identity.PayloadFiles)
        {
            if (!paths.Add(file.Path) || !IsApplicationPath(file.Path) || file.Sha256.Length != 64 || file.Sha256.Any(ch => !Uri.IsHexDigit(ch)))
                throw new InvalidDataException("installer.payload_manifest");
        }
        ValidateTools(identity);
        return identity;
    }

    private static bool IsApplicationPath(string path) => ApplicationPayload.SafePath(path)
        && (path is "NexusPipeline.exe" or UpdateRecipe.UpdateEntry or ApplicationPayload.LegacyUpdateEntry or "README.md" or ApplicationPayload.ManifestPath || path.StartsWith("resources/desktop/", StringComparison.Ordinal));

    internal static bool HasAbandonedEmptyRegistration()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(CurrentRegistryPath, false);
            if (key is null || key.GetValue("AppDir") is not null || key.GetSubKeyNames().Length != 0
                || key.GetValueNames().Any(name => name is not ("DataRoot" or "InstanceId" or "IdentityDigest"))
                || key.GetValue("DataRoot") is not string root || !Path.IsPathFullyQualified(root)
                || root.StartsWith(@"\\", StringComparison.Ordinal)
                || new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed) return false;
            using var uninstall = Registry.CurrentUser.OpenSubKey(CurrentUninstallRegistryPath, false);
            if (uninstall is not null) return false;
            RequireGenerationDestination(root);
            RequireLinkFree(ManagerDirectory);
            return IsAbsentOrEmptyDirectory(root) && IsAbsentOrEmptyDirectory(ManagerDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool IsAbsentOrEmptyDirectory(string path) => !File.Exists(path)
        && (!Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any());

    internal static bool ReleaseAbandonedEmptyRegistration(string root)
    {
        RequireGenerationDestination(root);
        if (!IsAbsentOrEmptyDirectory(root) || !HasAbandonedEmptyRegistration()) return false;
        string snapshot;
        using (var key = Registry.CurrentUser.OpenSubKey(CurrentRegistryPath, false))
            snapshot = JsonSerializer.Serialize(key!.GetValueNames().Order(StringComparer.Ordinal)
                .ToDictionary(name => name, name => new { Kind = key.GetValueKind(name), Value = key.GetValue(name) }));
        // Keep the original anchor outside the new installer's metadata transaction.
        RequireLinkFree(RegistrationRecoveryDirectory);
        Directory.CreateDirectory(RegistrationRecoveryDirectory);
        string backup = Path.Combine(RegistrationRecoveryDirectory, Digest(snapshot) + ".protected");
        RequireLinkFree(backup);
        if (File.Exists(backup))
        {
            string restored = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(File.ReadAllText(backup)), null, DataProtectionScope.CurrentUser));
            if (restored != snapshot) throw new IOException("installer.abandoned_registration_backup");
        }
        else JsonUtil.WriteAtomic(backup, Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(snapshot), null, DataProtectionScope.CurrentUser)));
        if (!HasAbandonedEmptyRegistration()) throw new IOException("installer.registration_changed");
        using (var key = Registry.CurrentUser.OpenSubKey(CurrentRegistryPath, false))
            if (JsonSerializer.Serialize(key!.GetValueNames().Order(StringComparer.Ordinal)
                .ToDictionary(name => name, name => new { Kind = key.GetValueKind(name), Value = key.GetValue(name) })) != snapshot)
                throw new IOException("installer.registration_changed");
        Registry.CurrentUser.DeleteSubKey(CurrentRegistryPath, false);
        return true;
    }

    internal static InstallerInstanceIdentity? Read(string root, bool requireActive = false)
    {
        ResumeManualMaintenance(root);
        ResumeManualAdoption(root);
        RequireGenerationDestination(root);
        RequireLinkFree(root); RequireLinkFree(ManagerDirectory); RequireLinkFree(IdentityPath);
        if (HasAbandonedEmptyRegistration()) return null;
        using var key = Registry.CurrentUser.OpenSubKey(CurrentRegistryPath, false);
        if (key?.GetValue("DataRoot") is string registeredRoot
            && !string.Equals(Path.GetFullPath(registeredRoot).TrimEnd('\\'), Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return null;
        if (key?.GetValue("IdentityDigest") is not string digest)
        {
            if (File.Exists(IdentityPath)) throw new IOException("installer.unanchored_identity");
            return null;
        }
        string seal = File.ReadAllText(IdentityPath).TrimStart('\uFEFF');
        if (Digest(seal) != digest) throw new IOException("installer.identity_digest");
        var identity = Unseal(seal, root, key.GetValue("InstanceId") as string ?? "", CurrentUserId);
        if (requireActive && identity.State != "active") throw new IOException("installer.instance_not_active");
        return identity;
    }

    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static void Save(InstallerInstanceIdentity identity, bool updateHelper = true, bool installTools = false, byte[]? frozenBytes = null)
    {
        RequireGenerationDestination(identity.AppRoot);
        RequireLinkFree(identity.AppRoot); RequireLinkFree(ManagerDirectory);
        Directory.CreateDirectory(ManagerDirectory);
        string seal = frozenBytes is null ? Seal(identity) : Encoding.UTF8.GetString(frozenBytes).TrimStart('\uFEFF');
        if (installTools)
        {
            InstallerMetadataCheckpoint.StageInstallerTools(identity, seal);
            InstallRegisteredTools(identity);
        }
        if (identity.State == "active" && updateHelper)
        {
            string? repair = RepairInstallerTools(identity);
            if (repair is not null) NexusPipeline.Maintenance.MaintenanceLog.Warn(repair);
        }
        if (frozenBytes is null) JsonUtil.WriteAtomic(IdentityPath, seal);
        else
        {
            string temporary = IdentityPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(frozenBytes); output.Flush(true); }
            File.Move(temporary, IdentityPath, true);
        }
        using var key = Registry.CurrentUser.CreateSubKey(CurrentRegistryPath, true);
        key.SetValue("InstanceId", identity.InstanceId); key.SetValue("IdentityDigest", Digest(seal));
        key.SetValue("DataRoot", identity.AppRoot);
        if (identity.State == "active") key.SetValue("AppDir", identity.AppRoot);
        else key.DeleteValue("AppDir", false);
    }

    internal static void Register(string root, string version, string manifestPath)
        => RegisterCore(root, version, manifestPath, (full, targetVersion) =>
        {
            _ = ApplicationPayload.Validate(full, targetVersion, installed: true);
            return CapturePayload(full);
        });

#if NEXUS_TEST_HOST
    internal static void RegisterOwnedInventoryForTest(string root, string version, string manifestPath,
        IReadOnlyList<InstalledPayloadFile> verifiedInventory)
        => RegisterCore(root, version, manifestPath, (_, _) => verifiedInventory, installTools: false);
#endif

    private static void RegisterCore(string root, string version, string manifestPath,
        Func<string, string, IReadOnlyList<InstalledPayloadFile>> inspectApplication, bool installTools = true)
    {
        string full = Path.GetFullPath(root).TrimEnd('\\');
        RequireLinkFree(full);
        var existing = Read(full);
        if (existing is null)
        {
            using var key = Registry.CurrentUser.OpenSubKey(CurrentRegistryPath, false);
            string? oldRoot = key?.GetValue("DataRoot") as string ?? key?.GetValue("AppDir") as string;
            if (oldRoot is not null && !string.Equals(Path.GetFullPath(oldRoot).TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase))
                throw new IOException("installer.another_instance_registered");
            existing = new(1, Guid.NewGuid().ToString("N"), full, CurrentUserId, DateTimeOffset.UtcNow, "active", version, []);
        }
        if (ConfigUpdateAdmission.HasPendingRecovery(Path.Combine(full, "data"))) throw new IOException("installer.configuration_recovery_pending");
        RequireLinkFree(manifestPath);
        if (new FileInfo(manifestPath).Length > 4 * 1024 * 1024) throw new IOException("installer.manifest_limit");
        var payload = JsonSerializer.Deserialize<InstalledPayloadFile[]>(File.ReadAllText(manifestPath))
            ?? throw new IOException("installer.manifest_missing");
        // Validate the supplied frozen Setup inventory before accepting ownership.
        _ = Unseal(Seal(existing with { PayloadFiles = payload }), full, existing.InstanceId, CurrentUserId);
        foreach (var item in payload)
        {
            string file = Path.Combine(full, item.Path.Replace('/', Path.DirectorySeparatorChar));
            RequireLinkFree(file);
            if (!File.Exists(file) || UpdateApply.ImageHash(file) != item.Sha256) throw new IOException("installer.payload_hash");
        }
        var application = inspectApplication(full, version);
        if (!payload.OrderBy(file => file.Path, StringComparer.Ordinal).SequenceEqual(application)) throw new IOException("installer.application_inventory");
        var updated = existing with { State = "active", Version = version, PayloadFiles = payload };
        if (installTools) updated = RegisterTools(updated);
        Save(updated, installTools: installTools);
    }

    private static IReadOnlyList<InstalledPayloadFile> CapturePayload(string root)
    {
        _ = ApplicationPayload.Validate(root, installed: true);
        return ApplicationPayload.Files(root).Select(file => new InstalledPayloadFile(file.Path, file.Sha256))
            .Append(new(ApplicationPayload.ManifestPath, UpdateApply.ImageHash(Path.Combine(root, ApplicationPayload.ManifestPath))))
            .OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        RequireLinkFree(root);
        if (!Directory.Exists(root)) yield break;
        var stack = new Stack<string>(); stack.Push(root);
        int count = 0;
        while (stack.TryPop(out string? directory))
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 20000) throw new IOException("installer.tree_limit");
                RequireLinkFree(child);
                if (Directory.Exists(child)) stack.Push(child); else yield return child;
            }
        }
    }

    internal static void SnapshotForUpdate(string root, string backup, bool includeHelper = false)
    {
        if (SnapshotBytesForUpdate(root) is { } bytes)
        {
            File.WriteAllBytes(Path.Combine(backup, ".installer-identity"), bytes);
            if (includeHelper && SnapshotHelperBytesForUpdate(root) is { } helper)
                File.WriteAllBytes(Path.Combine(backup, ".installer-helper"), helper);
        }
    }

    internal static byte[]? SnapshotBytesForUpdate(string root)
    {
        if (Read(root, true) is not { } identity) return null;
        RequireRegisteredHostOrAbsent(identity);
        _ = InspectInstallerTools(identity);
        _ = ApplicationPayload.FreezeForUpdate(root, identity.Version, installed: true);
        foreach (var file in identity.PayloadFiles)
        {
            string path = Path.Combine(root, file.Path);
            RequireLinkFree(path);
            if (!File.Exists(path) && file.Path is "README.md" or UpdateRecipe.UpdateEntry) continue;
            if (!File.Exists(path) || UpdateApply.ImageHash(path) != file.Sha256)
                throw new IOException("installer.manual_adoption_pending");
        }
        _ = SnapshotHelperBytesForUpdate(root);
        return File.ReadAllBytes(IdentityPath);
    }

    internal static byte[]? SnapshotHelperBytesForUpdate(string root)
    {
        if (Read(root) is not { } identity) return null;
        if (identity.State is not ("active" or "manual-adoption-pending")) throw new IOException("installer.instance_not_active");
        string helper = HelperPath(identity);
        RequireLinkFree(helper);
        if (Directory.Exists(helper)) throw new IOException("installer.unknown_helper_preserved");
        if (!File.Exists(helper)) return null;
        string? expected = HelperHash(identity);
        if (expected is null || new FileInfo(helper).Length > UpdatePackage.MaxSingleEntryBytes)
            throw new IOException("installer.unknown_helper_preserved");
        byte[] bytes = File.ReadAllBytes(helper);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expected)
            throw new IOException("installer.unknown_helper_preserved");
        return bytes;
    }

    internal static void RefreshAfterUpdate(string root, string version, bool setupTransition = false)
    {
        if (Read(root, true) is not { } identity) return;
        var updated = identity with { Version = version, PayloadFiles = CapturePayload(root) };
        if (setupTransition) { InstallerMetadataCheckpoint.PromoteTargetUninstaller(root, version); updated = RegisterTools(updated); }
        Save(updated, installTools: setupTransition);
    }

    internal static void RestoreAfterRollback(string root, string backup, UpdateTask? task = null)
    {
        string path = Path.Combine(backup, ".installer-identity");
        if (!File.Exists(path)) return;
        RequireLinkFree(path);
        using var key = Registry.CurrentUser.OpenSubKey(CurrentRegistryPath, false);
        var identity = Unseal(File.ReadAllText(path), root, key?.GetValue("InstanceId") as string ?? "", CurrentUserId);
        if (task is { JournalSchemaVersion: 3 or 4 })
        {
            task.BackupInventory!.RequireOwned(backup);
            string helper = HelperPath(identity);
            RequireLinkFree(helper);
            string? previousHash = HelperHash(identity);
            string? targetHash = Read(root)?.InstallerTools?.Worker.Sha256 ?? previousHash;
            string original = Path.Combine(backup, ".installer-helper");
            var snapshot = task.BackupInventory.Entries.SingleOrDefault(file => file.Path == ".installer-helper");
            if ((snapshot is null) != !File.Exists(original) || snapshot is not null
                && (snapshot.Sha256 != previousHash || new FileInfo(original).Length != snapshot.SizeBytes || UpdateApply.ImageHash(original) != snapshot.Sha256))
                throw new IOException("installer.helper_snapshot_changed");
            if (Directory.Exists(helper)) throw new IOException("installer.unknown_helper_preserved");
            if (File.Exists(helper))
            {
                string actual = UpdateApply.ImageHash(helper);
                if (actual != previousHash && actual != targetHash) throw new IOException("installer.unknown_helper_preserved");
                VerifiedFileDeletion.Delete(helper, new FileInfo(helper).Length, actual);
            }
            if (File.Exists(original)) File.Copy(original, helper, false);
            Save(identity, updateHelper: false, frozenBytes: File.ReadAllBytes(path));
            return;
        }
        Save(identity, updateHelper: false, frozenBytes: File.ReadAllBytes(path));
    }

    internal static void Uninstall(string root, bool deleteData)
    {
        var identity = Read(root, true) ?? throw new IOException("installer.instance_unknown");
        RequireRegisteredHostOrAbsent(identity);
        string full = identity.AppRoot;
        if (ConfigUpdateAdmission.HasPendingRecovery(Path.Combine(full, "data"))
            || File.Exists(Path.Combine(full, ".nxp-update", "task.json")) || Directory.Exists(Path.Combine(full, ".nxp-backup")))
            throw new IOException("installer.recovery_pending: export or recover the preserved evidence first");
        string[] dataPaths = ["config", "data", "history", "logs", ".nxp", "plugins", "outputs", "user-assets"];
        if (deleteData)
            foreach (string name in dataPaths) _ = EnumerateFiles(Path.Combine(full, name)).ToArray();
        foreach (var item in identity.PayloadFiles)
            RequireLinkFree(Path.Combine(full, item.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (identity.InstallerTools is { } tools && File.Exists(tools.PublicEntry.Path))
        {
            RequireLinkFree(tools.PublicEntry.Path);
            VerifiedFileDeletion.Delete(tools.PublicEntry.Path, new FileInfo(tools.PublicEntry.Path).Length, tools.PublicEntry.Sha256);
        }
        foreach (var item in identity.PayloadFiles)
        {
            string path = Path.Combine(full, item.Path.Replace('/', Path.DirectorySeparatorChar));
            RequireLinkFree(path);
            if (File.Exists(path) && UpdateApply.ImageHash(path) == item.Sha256) File.Delete(path);
        }
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in identity.PayloadFiles)
            for (string? path = Path.GetDirectoryName(Path.Combine(full, item.Path.Replace('/', Path.DirectorySeparatorChar)));
                 path is not null && !string.Equals(path, full, StringComparison.OrdinalIgnoreCase);
                 path = Path.GetDirectoryName(path))
                directories.Add(path);
        foreach (string path in directories.OrderByDescending(path => path.Length))
        {
            RequireLinkFree(path);
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
        if (deleteData)
            foreach (string name in dataPaths)
            {
                string path = Path.Combine(full, name); RequireLinkFree(path);
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
        if (!deleteData)
        {
            Save(identity with { State = "retained-data" });
            return;
        }
        if (Read(full, true)?.InstanceId != identity.InstanceId) throw new IOException("installer.registration_changed");
        File.Delete(IdentityPath);
        using (var key = Registry.CurrentUser.OpenSubKey(CurrentRegistryPath, true))
            foreach (string name in new[] { "DataRoot", "AppDir", "IdentityDigest", "InstanceId" }) key!.DeleteValue(name, false);
        using (var key = Registry.CurrentUser.OpenSubKey(CurrentRegistryPath, false))
            if (key!.GetValueNames().Length != 0 || key.GetSubKeyNames().Length != 0) return;
        Registry.CurrentUser.DeleteSubKey(CurrentRegistryPath, false);
    }
}
