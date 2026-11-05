using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Installation;

internal sealed record InstallerDataIntent(string Format, int Version, Guid TransactionId, string Root, string Profile,
    PhysicalObjectIdentity ManagerIdentity, PhysicalObjectIdentity DirectoryIdentity, byte[] Installation, byte[] Metadata,
    FileTreeSnapshot Runtime, PhysicalTreeObject[] Objects, ProcessIdentity Native, ProcessIdentity Writer, DateTimeOffset CreatedAt);
internal sealed record InstallerDataActor(string Format, int Version, Guid TransactionId, string IntentSha256,
    string ParentSha256, ProcessIdentity Native, ProcessIdentity Writer);

[SupportedOSPlatform("windows")]
internal static class InstallerDataCodec
{
    internal const int MaximumBytes = 33_554_432;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("nexus.installer.data-removal/g0180/1");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter<FileObjectKind>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    internal static byte[] Seal(InstallerDataIntent value) { Validate(value); return Protect(value); }
    internal static byte[] Seal(InstallerDataActor value) { Validate(value); return Protect(value); }
    internal static InstallerDataIntent Intent(byte[] bytes) { var value = Read<InstallerDataIntent>(bytes); Validate(value); return value; }
    internal static InstallerDataActor Actor(byte[] bytes) { var value = Read<InstallerDataActor>(bytes); Validate(value); return value; }
    internal static bool Equal(InstallerDataIntent left, InstallerDataIntent right)
        => JsonSerializer.Serialize(left, Options) == JsonSerializer.Serialize(right, Options);

    private static byte[] Protect<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > MaximumBytes) throw InstallationIdentityCodec.Invalid();
        try { return WindowsDataProtection.Transform(bytes, Entropy, protect: true); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static T Read<T>(byte[] sealedBytes) where T : class
    {
        if (sealedBytes.Length is < 1 or > MaximumBytes + 1024) throw InstallationIdentityCodec.Invalid();
        var bytes = WindowsDataProtection.Transform(sealedBytes, Entropy, protect: false);
        try
        {
            using var document = StrictJson.Parse(bytes, MaximumBytes, 1_048_576);
            return document.Deserialize<T>(Options) ?? throw InstallationIdentityCodec.Invalid();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void Validate(InstallerDataIntent value)
    {
        if (value.Format != "nexus.installer.data-removal" || value.Version != 1 || value.TransactionId == Guid.Empty ||
            value.Root is null || value.Root != PhysicalPathPolicy.NormalizeSyntax(value.Root) || value.Profile is not ("test" or "production") ||
            !InstallationIdentityCodec.Physical(value.ManagerIdentity) || !InstallationIdentityCodec.Physical(value.DirectoryIdentity) ||
            value.Installation is null || value.Metadata is null || value.CreatedAt == default || value.CreatedAt.Offset != TimeSpan.Zero)
            throw InstallationIdentityCodec.Invalid();
        var identity = InstallationIdentityCodec.Unseal(value.Installation, value.Root);
        RequireActor(value.Native, value.Writer, identity);
        var metadata = InstallerSnapshotCodec.Decode(value.Metadata, value.Root);
        if (metadata.RootIdentity != identity.RootIdentity || metadata.Directories.Single(item => item.Role == "manager").Identity != value.ManagerIdentity ||
            metadata.Files.Any(file => file.Role == "uninstall") || !metadata.Ownership.Exists || metadata.Ownership.Values.Length != 3)
            throw InstallationIdentityCodec.Invalid();
        var seal = metadata.Files.SingleOrDefault(file => file.Role == "manager" && file.Name == InstallationAnchor.IdentityFile);
        var worker = metadata.Files.SingleOrDefault(file => file.Role == "manager" && file.Name == InstallationAnchor.WorkerFile);
        var digest = Convert.ToHexStringLower(SHA256.HashData(value.Installation));
        if (seal?.Sha256 != digest || worker is null || worker.Identity != identity.Worker.Identity || worker.Sha256 != identity.Worker.Sha256 ||
            worker.SizeBytes != identity.Worker.SizeBytes || metadata.Ownership.Values.SingleOrDefault(item => item.Name == "IdentitySha256")?.Text != digest ||
            metadata.Ownership.Values.SingleOrDefault(item => item.Name == "ApplicationRoot")?.Text != identity.ApplicationRoot ||
            metadata.Ownership.Values.SingleOrDefault(item => item.Name == "InstallationId")?.Text != identity.InstallationId.ToString("D"))
            throw InstallationIdentityCodec.Invalid();
        ValidateTree(value.Runtime, value.Objects, identity.RuntimeIdentity);
    }

    private static void Validate(InstallerDataActor value)
    {
        if (value.Format != "nexus.installer.data-actor" || value.Version != 1 || value.TransactionId == Guid.Empty ||
            !InstallationIdentityCodec.Digest(value.IntentSha256) || !InstallationIdentityCodec.Digest(value.ParentSha256))
            throw InstallationIdentityCodec.Invalid();
        RequireProcess(value.Native); RequireProcess(value.Writer);
        if (value.Native.UserSid != value.Writer.UserSid || value.Native.IsElevated != value.Writer.IsElevated)
            throw InstallationIdentityCodec.Invalid();
    }

    internal static void RequireActor(ProcessIdentity native, ProcessIdentity writer, InstallationIdentity identity)
    {
        RequireProcess(native); RequireProcess(writer);
        if (native.ImageSha256 != identity.NativeEngine.Sha256 || writer.ImageSha256 != identity.Worker.Sha256 ||
            native.IsElevated != writer.IsElevated) throw InstallationIdentityCodec.Invalid();
    }

    private static void RequireProcess(ProcessIdentity value)
    {
        if (value is null || value.ProcessId <= 0 || value.CreatedAtUtc == default || value.CreatedAtUtc.Kind != DateTimeKind.Utc ||
            value.UserSid != InstallationIdentityCodec.UserSid || !InstallationIdentityCodec.Digest(value.ImageSha256) ||
            value.ImagePath is null || value.ImagePath != PhysicalPathPolicy.NormalizeSyntax(value.ImagePath)) throw InstallationIdentityCodec.Invalid();
    }

    private static void ValidateTree(FileTreeSnapshot tree, PhysicalTreeObject[] objects, PhysicalObjectIdentity runtime)
    {
        if (tree is null || tree.Shape != FileObjectKind.Directory || tree.Entries is null || tree.Entries.Count is < 3 or > 200000 ||
            objects is null || objects.Length != tree.Entries.Count) throw InstallationIdentityCodec.Invalid();
        var entries = new Dictionary<string, FileTreeEntry>(StringComparer.OrdinalIgnoreCase);
        var identities = new Dictionary<string, PhysicalObjectIdentity>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in tree.Entries)
        {
            if (entry is null || entry.RelativePath is null || !entries.TryAdd(entry.RelativePath, entry)) throw InstallationIdentityCodec.Invalid();
            if (entry.RelativePath.Length > 0) PhysicalPathPolicy.ValidateRelativePath(entry.RelativePath);
            if (entry.Kind == FileObjectKind.Directory ? entry.SizeBytes != 0 || entry.Sha256 is not null :
                entry.Kind != FileObjectKind.File || entry.SizeBytes is < 0 or > 1_073_741_824 || !InstallationIdentityCodec.Digest(entry.Sha256))
                throw InstallationIdentityCodec.Invalid();
            total = checked(total + entry.SizeBytes);
            if (total > InstallerRuntimeRemoval.Budget.MaximumTotalBytes) throw InstallationIdentityCodec.Invalid();
        }
        foreach (var item in objects)
            if (item is null || !InstallationIdentityCodec.Physical(item.Identity) || !entries.ContainsKey(item.RelativePath) ||
                !identities.TryAdd(item.RelativePath, item.Identity)) throw InstallationIdentityCodec.Invalid();
        if (entries.GetValueOrDefault("")?.Kind != FileObjectKind.Directory || identities.GetValueOrDefault("") != runtime ||
            entries.GetValueOrDefault("instance.json")?.Kind != FileObjectKind.File ||
            entries.GetValueOrDefault("instance.lock") is not { Kind: FileObjectKind.File, SizeBytes: 0 }) throw InstallationIdentityCodec.Invalid();
        foreach (var entry in entries.Values.Where(entry => entry.RelativePath.Length > 0))
        {
            var separator = entry.RelativePath.LastIndexOf('/');
            var parent = separator < 0 ? "" : entry.RelativePath[..separator];
            if (entries.GetValueOrDefault(parent)?.Kind != FileObjectKind.Directory) throw InstallationIdentityCodec.Invalid();
        }
    }
}
