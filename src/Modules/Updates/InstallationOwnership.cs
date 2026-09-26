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
    IReadOnlyList<InstalledPayloadFile> PayloadFiles);

/// <summary>Setup instance and retained-data ownership; updates keep this same payload manifest current.</summary>
internal static class InstallationOwnership
{
    internal const string RegistryPath = @"Software\NexusPipeline\Installer";
    internal static string CurrentRegistryPath => TestScope() is { } scope ? @"Software\NexusPipeline\Tests\Installer\" + scope.Id : RegistryPath;
    internal static string ManagerDirectory => TestScope() is { } scope ? Path.Combine(scope.Root, "manager")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusPipeline", "installer");
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

    internal static string Seal(InstallerInstanceIdentity identity) => Convert.ToBase64String(ProtectedData.Protect(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity)), null, DataProtectionScope.CurrentUser));

    internal static InstallerInstanceIdentity Unseal(string seal, string expectedRoot, string instanceId, string userId)
    {
        var identity = JsonSerializer.Deserialize<InstallerInstanceIdentity>(ProtectedData.Unprotect(
            Convert.FromBase64String(seal), null, DataProtectionScope.CurrentUser)) ?? throw new InvalidDataException("installer.identity_empty");
        if (identity.SchemaVersion != 1 || !Guid.TryParseExact(identity.InstanceId, "N", out _)
            || identity.InstanceId != instanceId || identity.WindowsUserId != userId
            || identity.CreatedAtUtc == default || identity.State is not ("active" or "retained-data")
            || !string.Equals(Path.GetFullPath(identity.AppRoot).TrimEnd('\\'), Path.GetFullPath(expectedRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            || identity.PayloadFiles.Count > 20000)
            throw new InvalidDataException("installer.identity_mismatch");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in identity.PayloadFiles)
        {
            if (!paths.Add(file.Path) || !IsApplicationPath(file.Path) || file.Sha256.Length != 64 || file.Sha256.Any(ch => !Uri.IsHexDigit(ch)))
                throw new InvalidDataException("installer.payload_manifest");
        }
        return identity;
    }

    private static bool IsApplicationPath(string path) => !Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains(':')
        && !path.Any(char.IsControl)
        && path.Split('/').All(part => part.Length > 0 && part is not ("." or ".."))
        && (path is "nexus-pipeline.exe" or "README.md" || path.StartsWith("wwwroot/", StringComparison.Ordinal));

    internal static InstallerInstanceIdentity? Read(string root, bool requireActive = false)
    {
        RequireLinkFree(root); RequireLinkFree(ManagerDirectory); RequireLinkFree(IdentityPath);
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

    private static void Save(InstallerInstanceIdentity identity)
    {
        RequireLinkFree(identity.AppRoot); RequireLinkFree(ManagerDirectory);
        Directory.CreateDirectory(ManagerDirectory);
        if (identity.State == "active")
        {
            string source = Path.Combine(identity.AppRoot, "nexus-pipeline.exe");
            string helper = Path.Combine(ManagerDirectory, "nexus-installer-helper.exe");
            RequireLinkFree(helper);
            if (File.Exists(helper))
            {
                var previous = Read(identity.AppRoot);
                string? expected = previous?.PayloadFiles.SingleOrDefault(file => file.Path == "nexus-pipeline.exe")?.Sha256;
                if (expected is null || UpdateApply.ImageHash(helper) != expected)
                    throw new IOException("installer.unknown_helper_preserved");
            }
            string temp = helper + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.Copy(source, temp, false);
            File.Move(temp, helper, true);
        }
        string seal = Seal(identity);
        JsonUtil.WriteAtomic(IdentityPath, seal);
        using var key = Registry.CurrentUser.CreateSubKey(CurrentRegistryPath, true);
        key.SetValue("InstanceId", identity.InstanceId); key.SetValue("IdentityDigest", Digest(seal));
        key.SetValue("DataRoot", identity.AppRoot);
        if (identity.State == "active") key.SetValue("AppDir", identity.AppRoot);
        else key.DeleteValue("AppDir", false);
    }

    internal static void Register(string root, string version, string manifestPath)
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
        Save(existing with { State = "active", Version = version, PayloadFiles = payload });
    }

    private static IReadOnlyList<InstalledPayloadFile> CapturePayload(string root)
    {
        var result = new List<InstalledPayloadFile>();
        foreach (string name in new[] { "nexus-pipeline.exe", "README.md" })
        {
            string path = Path.Combine(root, name); RequireLinkFree(path);
            if (File.Exists(path)) result.Add(new(name, UpdateApply.ImageHash(path)));
        }
        string web = Path.Combine(root, "wwwroot");
        foreach (string file in EnumerateFiles(web))
            result.Add(new(Path.GetRelativePath(root, file).Replace('\\', '/'), UpdateApply.ImageHash(file)));
        if (!result.Any(file => file.Path == "nexus-pipeline.exe")) throw new IOException("installer.payload_missing");
        return result.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
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

    internal static void SnapshotForUpdate(string root, string backup)
    {
        if (Read(root, true) is not { } identity) return;
        string seal = Seal(identity);
        File.WriteAllText(Path.Combine(backup, ".installer-identity"), seal, new UTF8Encoding(false));
    }

    internal static void RefreshAfterUpdate(string root, string version)
    {
        if (Read(root, true) is not { } identity) return;
        Save(identity with { Version = version, PayloadFiles = CapturePayload(root) });
    }

    internal static void RestoreAfterRollback(string root, string backup)
    {
        string path = Path.Combine(backup, ".installer-identity");
        if (!File.Exists(path)) return;
        RequireLinkFree(path);
        using var key = Registry.CurrentUser.OpenSubKey(CurrentRegistryPath, false);
        var identity = Unseal(File.ReadAllText(path), root, key?.GetValue("InstanceId") as string ?? "", CurrentUserId);
        Save(identity);
    }

    internal static void Uninstall(string root, bool deleteData)
    {
        var identity = Read(root, true) ?? throw new IOException("installer.instance_unknown");
        string full = identity.AppRoot;
        if (ConfigUpdateAdmission.HasPendingRecovery(Path.Combine(full, "data"))
            || File.Exists(Path.Combine(full, ".nxp-update", "task.json")) || Directory.Exists(Path.Combine(full, ".nxp-backup")))
            throw new IOException("installer.recovery_pending: export or recover the preserved evidence first");
        string[] dataPaths = ["config", "data", "history", "logs", ".nxp", "plugins", "outputs", "user-assets"];
        if (deleteData)
            foreach (string name in dataPaths) _ = EnumerateFiles(Path.Combine(full, name)).ToArray();
        foreach (var item in identity.PayloadFiles)
            RequireLinkFree(Path.Combine(full, item.Path.Replace('/', Path.DirectorySeparatorChar)));
        foreach (var item in identity.PayloadFiles)
        {
            string path = Path.Combine(full, item.Path.Replace('/', Path.DirectorySeparatorChar));
            RequireLinkFree(path);
            if (File.Exists(path) && UpdateApply.ImageHash(path) == item.Sha256) File.Delete(path);
        }
        if (deleteData)
            foreach (string name in dataPaths)
            {
                string path = Path.Combine(full, name); RequireLinkFree(path);
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
        Save(identity with { State = "retained-data" });
    }
}
