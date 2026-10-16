using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Updates;

internal static partial class InstallationOwnership
{
    private sealed record MaintenanceCheckpoint(int SchemaVersion, string Root, string BeforeSeal, string AfterSeal);
    private static string MaintenancePath => Path.Combine(ManagerDirectory, "manual-maintenance.protected");

    private static void SuspendManualMaintenance(string root, InstallerInstanceIdentity identity, Action<string>? checkpoint)
    {
        if (File.Exists(Path.Combine(root, ".nxp-update", "task.json")) || Directory.Exists(Path.Combine(root, ".nxp-backup")))
            throw new IOException("installer.recovery_pending");
        string before = File.ReadAllText(IdentityPath).TrimStart('\uFEFF');
        string after = Seal(identity with { State = "manual-adoption-pending" });
        var state = new MaintenanceCheckpoint(1, Path.GetFullPath(root).TrimEnd('\\'), before, after);
        PublishProtectedCheckpoint(MaintenancePath, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(state), null, DataProtectionScope.CurrentUser));
        checkpoint?.Invoke("maintenance-prepared");
        ResumeManualMaintenanceCore(root, checkpoint);
    }

    private static void ResumeManualMaintenance(string root)
        => WithAdoptionLock(() => ResumeManualMaintenanceCore(root, null));

    internal static void RestoreManualBaseline(string root, Action<string>? checkpoint = null)
        => WithAdoptionLock(() =>
        {
            var identity = Read(root) ?? throw new IOException("installer.instance_unowned");
            if (identity.State != "manual-adoption-pending") throw new IOException("installer.adoption_not_pending");
            RequireOriginalPayload(root, identity);
            string before = File.ReadAllText(IdentityPath).TrimStart('\uFEFF');
            string after = Seal(identity with { State = "active" });
            var state = new MaintenanceCheckpoint(1, Path.GetFullPath(root).TrimEnd('\\'), before, after);
            PublishProtectedCheckpoint(MaintenancePath, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(state), null, DataProtectionScope.CurrentUser));
            ResumeManualMaintenanceCore(root, checkpoint);
        });

    private static void RequireOriginalPayload(string root, InstallerInstanceIdentity identity)
    {
        _ = ApplicationPayload.Freeze(root, identity.Version, installed: true);
        foreach (var file in identity.PayloadFiles)
        {
            string path = Path.Combine(root, file.Path);
            RequireLinkFree(path);
            if (!File.Exists(path) || UpdateApply.ImageHash(path) != file.Sha256)
                throw new IOException("installer.original_payload_not_restored");
        }
        string helper = HelperPath(identity);
        RequireLinkFree(helper);
        if (Directory.Exists(helper) || File.Exists(helper) && UpdateApply.ImageHash(helper)
            != HelperHash(identity))
            throw new IOException("installer.unknown_helper_preserved");
    }

    private static void ResumeManualMaintenanceCore(string root, Action<string>? checkpoint)
    {
        RequireLinkFree(MaintenancePath);
        if (!File.Exists(MaintenancePath)) return;
        if (new FileInfo(MaintenancePath).Length > 8 * 1024 * 1024) throw new IOException("installer.maintenance_checkpoint_limit");
        byte[] bytes = File.ReadAllBytes(MaintenancePath);
        var state = JsonSerializer.Deserialize<MaintenanceCheckpoint>(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser),
            new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new IOException("installer.maintenance_checkpoint_invalid");
        if (!string.Equals(state.Root, Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        if (state.SchemaVersion != 1) throw new IOException("installer.maintenance_checkpoint_schema");
        RequireGenerationDestination(root);
        RequireLinkFree(IdentityPath);
        using var key = Registry.CurrentUser.OpenSubKey(CurrentRegistryPath, true) ?? throw new IOException("installer.adoption_anchor_missing");
        string id = key.GetValue("InstanceId") as string ?? "";
        var before = Unseal(state.BeforeSeal, root, id, CurrentUserId);
        var after = Unseal(state.AfterSeal, root, id, CurrentUserId);
        string? digest = key.GetValue("IdentityDigest") as string;
        string active = File.ReadAllText(IdentityPath).TrimStart('\uFEFF');
        bool restoring = before.State == "manual-adoption-pending" && after.State == "active";
        if (!(before.State == "active" && after.State == "manual-adoption-pending") && !restoring
            || JsonSerializer.Serialize(before with { State = after.State }) != JsonSerializer.Serialize(after)
            || digest != Digest(state.BeforeSeal) && digest != Digest(state.AfterSeal)
            || active != state.BeforeSeal && active != state.AfterSeal
            || key.GetValue("DataRoot") is not string registered || !string.Equals(registered, root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("installer.maintenance_anchor_changed");
        if (restoring) RequireOriginalPayload(root, before);
        // Both seals remain recoverable until the matching registry anchor is published.
        JsonUtil.WriteAtomic(IdentityPath, state.AfterSeal);
        checkpoint?.Invoke("maintenance-identity");
        key.SetValue("IdentityDigest", Digest(state.AfterSeal));
        checkpoint?.Invoke("maintenance-anchor");
        VerifiedFileDeletion.Delete(MaintenancePath, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}
