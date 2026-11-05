using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Installation;

internal sealed record InstallerCheckpoint(string Format, int Version, Guid TransactionId, string Stage,
    string Root, string Profile, string Scope, string ManifestSha256, string WorkerSha256, PhysicalObjectIdentity ManagerIdentity,
    PhysicalObjectIdentity CheckpointIdentity, PhysicalObjectIdentity BackupIdentity, ProcessIdentity SetupOwner, ProcessIdentity Writer,
    DateTimeOffset CreatedAt, byte[] Before, byte[]? After, string? ParentSha256)
{
    public byte[]? TargetManifest { get; init; }
    public InstallerOwnedSource? OwnedSource { get; init; }
    public PhysicalObjectIdentity? PayloadIdentity { get; init; }
    public InstallerApplicationTarget? TargetApplication { get; init; }
}

[SupportedOSPlatform("windows")]
internal static class InstallerCheckpointCodec
{
    internal const int MaximumProtectedBytes = 5 * 1024 * 1024;
    private const int MaximumPlainBytes = 4 * 1024 * 1024;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("nexus.installer.checkpoint/g0180/1");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true
    };

    internal static byte[] Seal(InstallerCheckpoint value)
    {
        Validate(value);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > MaximumPlainBytes) throw InstallationIdentityCodec.Invalid();
        try { return WindowsDataProtection.Transform(bytes, Entropy, protect: true, maximumOutputBytes: MaximumProtectedBytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static InstallerCheckpoint Unseal(byte[] sealedBytes)
    {
        if (sealedBytes.Length is < 1 or > MaximumProtectedBytes) throw InstallationIdentityCodec.Invalid();
        var bytes = WindowsDataProtection.Transform(sealedBytes, Entropy, protect: false, maximumOutputBytes: MaximumPlainBytes);
        try
        {
            using var document = StrictJson.Parse(bytes, MaximumPlainBytes, 3 * 1024 * 1024);
            var value = document.Deserialize<InstallerCheckpoint>(Options) ?? throw InstallationIdentityCodec.Invalid();
            Validate(value);
            return value;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void Validate(InstallerCheckpoint value)
    {
        if (value.Format != "nexus.installer.checkpoint" || value.Version != 1 || value.TransactionId == Guid.Empty ||
            value.Stage is not ("prepared" or "payload" or "target" or "planned" or "observed") || value.Profile is not ("production" or "test") ||
            value.Scope is not ("installation" or "ownership") ||
            value.Root is null || value.Root != PhysicalPathPolicy.NormalizeSyntax(value.Root) ||
            !InstallationIdentityCodec.Digest(value.ManifestSha256) || !InstallationIdentityCodec.Digest(value.WorkerSha256) ||
            !InstallationIdentityCodec.Physical(value.ManagerIdentity) ||
            !InstallationIdentityCodec.Physical(value.CheckpointIdentity) || !InstallationIdentityCodec.Physical(value.BackupIdentity) ||
            value.CreatedAt == default || value.CreatedAt.Offset != TimeSpan.Zero ||
            value.Before is null || value.Before.Length is < 1 or > InstallerSnapshotCodec.MaximumBytes)
            throw InstallationIdentityCodec.Invalid();
        RequireProcess(value.SetupOwner); RequireProcess(value.Writer);
        if (value.TargetManifest is not null)
        {
            if (value.Scope != "installation") throw InstallationIdentityCodec.Invalid();
            _ = InstallerTargetManifest.Read(value);
        }
        if (value.SetupOwner.IsElevated != value.Writer.IsElevated) throw InstallationIdentityCodec.Invalid();
        var before = InstallerSnapshotCodec.Decode(value.Before, value.Root);
        RequireScope(value.Scope, before);
        if (value.OwnedSource is not null) InstallerSourceBinding.Require(value);
        if (before.RootIdentity is null) throw InstallationIdentityCodec.Invalid();
        if (value.PayloadIdentity is not null && (value.OwnedSource is null ||
            !InstallationIdentityCodec.Physical(value.PayloadIdentity) || value.PayloadIdentity.VolumeSerial != before.RootIdentity.VolumeSerial) ||
            value.Stage == "payload" && value.PayloadIdentity is null || value.Stage == "prepared" && value.PayloadIdentity is not null)
            throw InstallationIdentityCodec.Invalid();
        var manager = before.Directories.Single(directory => directory.Role == "manager").Identity;
        if (manager is not null && manager != value.ManagerIdentity) throw InstallationIdentityCodec.Invalid();
        if (value.TargetApplication is { } target)
        {
            if (value.PayloadIdentity is null || value.Stage is "prepared" or "payload" || target.Snapshot is null || target.Objects is null ||
                target.Snapshot.Entries is null || target.Snapshot.Entries.Any(item => item is null || item.RelativePath is null) ||
                target.Objects.Any(item => item is null || item.RelativePath is null)) throw InstallationIdentityCodec.Invalid();
            ApplicationTree.Validate(target.Snapshot, complete: true);
            InstallerTargetManifest.Read(value).RequireInstalled(target.Snapshot);
            FileTreeArchive.ValidateArchive(InstallerApplicationObjects.Tree(target.Snapshot, target.Objects));
            if (target.Objects.Any(item => !InstallationIdentityCodec.Physical(item.Identity)) ||
                !target.Objects.Select(item => item.RelativePath).SequenceEqual(target.Snapshot.Entries.Select(item => item.RelativePath)))
                throw InstallationIdentityCodec.Invalid();
        }
        else if (value.Stage == "target") throw InstallationIdentityCodec.Invalid();
        if (value.Stage is "prepared" or "payload" or "target")
        {
            if (value.After is not null || (value.Stage == "prepared" ? value.ParentSha256 is not null :
                !InstallationIdentityCodec.Digest(value.ParentSha256))) throw InstallationIdentityCodec.Invalid();
        }
        else
        {
            if (value.After is null || value.After.Length is < 1 or > InstallerSnapshotCodec.MaximumBytes ||
                !InstallationIdentityCodec.Digest(value.ParentSha256)) throw InstallationIdentityCodec.Invalid();
            var after = InstallerSnapshotCodec.Decode(value.After, value.Root);
            RequireScope(value.Scope, after);
            if (value.Scope == "ownership" && (!InstallerMetadataInventory.Equal(before,
                after with { Files = before.Files, Ownership = before.Ownership }) ||
                !before.Files.Where(file => file.Role != "manager").SequenceEqual(after.Files.Where(file => file.Role != "manager"))))
                throw InstallationIdentityCodec.Invalid();
            if (after.RootIdentity != before.RootIdentity ||
                after.Directories.Single(directory => directory.Role == "manager").Identity != value.ManagerIdentity)
                throw InstallationIdentityCodec.Invalid();
        }
    }

    private static void RequireScope(string scope, InstallerMetadataSnapshot snapshot)
    {
        // The native uninstaller exclusively owns its log for its entire script lifecycle.
        if (scope == "ownership" && snapshot.Files.Any(file => file.Role == "uninstall")) throw InstallationIdentityCodec.Invalid();
    }

    private static void RequireProcess(ProcessIdentity value)
    {
        if (value is null || value.ProcessId <= 0 || value.CreatedAtUtc.Kind != DateTimeKind.Utc || value.CreatedAtUtc == default ||
            value.UserSid != InstallationIdentityCodec.UserSid || !InstallationIdentityCodec.Digest(value.ImageSha256) ||
            value.ImagePath is null || value.ImagePath != PhysicalPathPolicy.NormalizeSyntax(value.ImagePath))
            throw InstallationIdentityCodec.Invalid();
    }
}
