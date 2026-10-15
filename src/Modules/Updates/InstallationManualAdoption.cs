using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Versioning;

namespace NexusPipeline.Modules.Updates;

internal static partial class InstallationOwnership
{
    private sealed record AdoptionCheckpoint(int SchemaVersion, string Root, string BeforeSeal, string AfterSeal,
        string? PreviousHelperHash, VerifiedManualAdoption Proof);
    private static string AdoptionPath => Path.Combine(ManagerDirectory, "manual-adoption.protected");

    private static void RequireRegisteredHostOrAbsent(InstallerInstanceIdentity identity)
    {
        string image = Path.Combine(identity.AppRoot, "NexusPipeline.exe");
        RequireLinkFree(image);
        string? expected = identity.PayloadFiles.SingleOrDefault(file => file.Path == "NexusPipeline.exe")?.Sha256;
        if (File.Exists(image) && UpdateApply.ImageHash(image) != expected)
            throw new IOException("installer.manual_adoption_pending");
    }

    private static void ValidateAdoptionProof(InstallerInstanceIdentity before, string version, VerifiedManualAdoption proof)
    {
        proof.Release.Validate(version, proof.Payload);
        proof.Transport.Validate(proof.Release);
        if (proof.Transport.Kind != "full" || proof.Transport.SourceManifestSha256 != proof.Payload.ManifestSha256
            || !UpdateInventory.Hash(proof.CoverRecipeSha256) || proof.Base.ReleaseTag != "v" + before.Version
            || proof.Base.PayloadManifestSha256 != before.PayloadFiles.SingleOrDefault(file => file.Path == ApplicationPayload.ManifestPath)?.Sha256)
            throw new IOException("installer.adoption_proof_invalid");
    }

    internal static InstallerInstanceIdentity? PendingManualAdoption(string root, string currentVersion, Action<string>? checkpoint = null)
        => WithAdoptionLock(() => PendingManualAdoptionCore(root, currentVersion, checkpoint));

    private static InstallerInstanceIdentity? PendingManualAdoptionCore(string root, string currentVersion, Action<string>? checkpoint)
    {
        var identity = Read(root);
        if (identity is null) return null;
        if (identity.State is not ("active" or "manual-adoption-pending")) throw new IOException("installer.instance_not_active");
        if (identity.Version == currentVersion && identity.State == "active") return null;
        if (!NexusVersion.TryParse(identity.Version, out var previous) || !NexusVersion.TryParse(currentVersion, out var current)
            || previous.CompareTo(NexusVersion.Stable(0, 17, 3)) < 0 || current.CompareTo(previous) <= 0)
            throw new IOException("installer.adoption_route_unavailable");
        _ = SnapshotHelperBytesForUpdate(root);
        if (identity.State == "active") SuspendManualMaintenance(root, identity, checkpoint);
        _ = ApplicationPayload.FreezeForUpdate(root, currentVersion, installed: true);
        return Read(root) ?? throw new IOException("installer.adoption_owner_missing");
    }

    internal static void CommitManualAdoption(string root, InstallerInstanceIdentity expected, VerifiedManualAdoption proof,
        Action<string>? checkpoint = null)
        => WithAdoptionLock(() => CommitManualAdoptionCore(root, expected, proof, checkpoint));

    private static void CommitManualAdoptionCore(string root, InstallerInstanceIdentity expected, VerifiedManualAdoption proof,
        Action<string>? checkpoint)
    {
        var current = Read(root) ?? throw new IOException("installer.adoption_owner_missing");
        if (JsonSerializer.Serialize(current) != JsonSerializer.Serialize(expected)) throw new IOException("installer.adoption_owner_changed");
        string version = proof.Release.Tag[1..];
        if (PendingManualAdoption(root, version) is null) throw new IOException("installer.adoption_not_pending");
        if (ApplicationPayload.Freeze(root, version, installed: true) != proof.Payload) throw new IOException("installer.adoption_payload_changed");
        ValidateAdoptionProof(current, version, proof);
        string before = File.ReadAllText(IdentityPath).TrimStart('\uFEFF');
        var after = current with { State = "active", Version = version, PayloadFiles = CapturePayload(root) };
        string? helperHash = SnapshotHelperBytesForUpdate(root) is { } helper ? Convert.ToHexStringLower(SHA256.HashData(helper)) : null;
        var state = new AdoptionCheckpoint(1, Path.GetFullPath(root).TrimEnd('\\'), before, Seal(after), helperHash, proof);
        string temporary = Path.Combine(ManagerDirectory, "nexus-installer-helper.exe.adoption.tmp");
        RequireLinkFree(temporary);
        if (File.Exists(temporary) || Directory.Exists(temporary)) throw new IOException("installer.adoption_helper_conflict");
        RequireLinkFree(AdoptionPath);
        byte[] bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(state), null, DataProtectionScope.CurrentUser);
        PublishProtectedCheckpoint(AdoptionPath, bytes);
        checkpoint?.Invoke("prepared");
        ResumeManualAdoption(root, checkpoint);
    }

    private static void PublishProtectedCheckpoint(string path, byte[] bytes)
    {
        RequireLinkFree(path);
        string prepared = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(prepared, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        try { File.Move(prepared, path, false); }
        finally
        {
            if (File.Exists(prepared))
                VerifiedFileDeletion.Delete(prepared, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    internal static void ResumeManualAdoption(string root, Action<string>? checkpoint = null)
        => WithAdoptionLock(() => ResumeManualAdoptionCore(root, checkpoint));

    private static void WithAdoptionLock(Action action)
    {
        using var mutex = new Mutex(false, @"Local\NexusPipeline.Adoption." + Digest(CurrentUserId));
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("installer.adoption_busy");
        try { action(); }
        finally { mutex.ReleaseMutex(); }
    }

    private static T WithAdoptionLock<T>(Func<T> action)
    {
        T result = default!;
        WithAdoptionLock(() => { result = action(); });
        return result;
    }

    private static void ResumeManualAdoptionCore(string root, Action<string>? checkpoint)
    {
        root = Path.GetFullPath(root).TrimEnd('\\');
        RequireLinkFree(AdoptionPath);
        if (!File.Exists(AdoptionPath)) return;
        if (new FileInfo(AdoptionPath).Length > 8 * 1024 * 1024) throw new IOException("installer.adoption_checkpoint_limit");
        byte[] journal = File.ReadAllBytes(AdoptionPath);
        var state = JsonSerializer.Deserialize<AdoptionCheckpoint>(ProtectedData.Unprotect(journal, null, DataProtectionScope.CurrentUser),
            new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new IOException("installer.adoption_checkpoint_invalid");
        if (!string.Equals(state.Root, Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        if (state.SchemaVersion != 1) throw new IOException("installer.adoption_checkpoint_schema");
        RequireGenerationDestination(root);
        RequireLinkFree(IdentityPath);
        using var key = Registry.CurrentUser.OpenSubKey(CurrentRegistryPath, true) ?? throw new IOException("installer.adoption_anchor_missing");
        string id = key.GetValue("InstanceId") as string ?? "";
        var before = Unseal(state.BeforeSeal, root, id, CurrentUserId);
        var after = Unseal(state.AfterSeal, root, id, CurrentUserId);
        string? digest = key.GetValue("IdentityDigest") as string;
        string active = File.ReadAllText(IdentityPath).TrimStart('\uFEFF');
        if (digest != Digest(state.BeforeSeal) && digest != Digest(state.AfterSeal)
            || active != state.BeforeSeal && active != state.AfterSeal
            || key.GetValue("DataRoot") is not string registered || !string.Equals(registered, root, StringComparison.OrdinalIgnoreCase)
            || before.State is not ("active" or "manual-adoption-pending") || after.State != "active" || before.CreatedAtUtc != after.CreatedAtUtc
            || state.PreviousHelperHash is not null && state.PreviousHelperHash != before.PayloadFiles.SingleOrDefault(file => file.Path == "NexusPipeline.exe")?.Sha256
            || !NexusVersion.TryParse(before.Version, out var source) || !NexusVersion.TryParse(after.Version, out var target)
            || source.CompareTo(NexusVersion.Stable(0, 17, 3)) < 0 || target.CompareTo(source) <= 0)
            throw new IOException("installer.adoption_anchor_changed");
        ValidateAdoptionProof(before, after.Version, state.Proof);
        if (ApplicationPayload.Freeze(root, after.Version, installed: true) != state.Proof.Payload
            || !CapturePayload(root).SequenceEqual(after.PayloadFiles)) throw new IOException("installer.adoption_payload_changed");
        string image = Path.Combine(root, "NexusPipeline.exe"), helperPath = Path.Combine(ManagerDirectory, "nexus-installer-helper.exe");
        string targetHash = after.PayloadFiles.Single(file => file.Path == "NexusPipeline.exe").Sha256;
        RequireLinkFree(helperPath);
        if (Directory.Exists(helperPath)) throw new IOException("installer.unknown_helper_preserved");
        string? observed = File.Exists(helperPath) ? UpdateApply.ImageHash(helperPath) : null;
        if (observed != targetHash && observed != state.PreviousHelperHash) throw new IOException("installer.unknown_helper_preserved");
        if (observed != targetHash)
        {
            string temporary = helperPath + ".adoption.tmp";
            RequireLinkFree(temporary);
            if (File.Exists(temporary))
            {
                if (UpdateApply.ImageHash(temporary) != targetHash) throw new IOException("installer.adoption_helper_conflict");
            }
            else File.Copy(image, temporary, false);
            if (UpdateApply.ImageHash(temporary) != targetHash) throw new IOException("installer.adoption_helper_changed");
            File.Move(temporary, helperPath, true);
        }
        checkpoint?.Invoke("helper");
        JsonUtil.WriteAtomic(IdentityPath, state.AfterSeal);
        checkpoint?.Invoke("identity");
        key.SetValue("IdentityDigest", Digest(state.AfterSeal));
        checkpoint?.Invoke("anchor");
        VerifiedFileDeletion.Delete(AdoptionPath, journal.Length, Convert.ToHexStringLower(SHA256.HashData(journal)));
    }
}
