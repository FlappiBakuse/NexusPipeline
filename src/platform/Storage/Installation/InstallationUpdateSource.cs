using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Maintenance;

namespace NexusPipeline.Platform.Storage.Installation;

internal sealed record InstallationUpdateSource(string Format, int Version, string Profile, string ManagerRoot,
    string RegistryKey, byte[] Metadata, byte[] Installation);

[SupportedOSPlatform("windows")]
internal sealed class InstallationUpdateBinding(RuntimeInstance instance, ContractReader contracts)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, NumberHandling = JsonNumberHandling.Strict,
        RespectRequiredConstructorParameters = true
    };
    private readonly ImmutableBlobStore _blobs = new(instance, contracts);

    internal BlobReference? Capture(string root, ArchivedFileTree before, string profile)
    {
        var paths = CurrentPaths();
        if (paths is null || !Registered(paths, root)) return null;
        var identity = new InstallationAnchor(paths.ManagerRoot, paths.RegistryKey).Read(root) ?? throw Invalid();
        if (identity.State != "active") throw Invalid();
        RequireMembers(paths, checkpoint: false);
        var metadata = InstallerMetadataInventory.Capture(root, paths);
        InstallerRegistration.RequireNative(metadata, identity.ProductVersion);
        var path = Path.Combine(paths.ManagerRoot, InstallationAnchor.IdentityFile);
        var proof = metadata.Files.Single(file => file.Role == "manager" && file.Name == InstallationAnchor.IdentityFile);
        using var held = InstallerMetadataBackup.HoldFile(path, proof, proof.ParentIdentity);
        var bytes = new byte[checked((int)held.Length)]; held.Position = 0; held.ReadExactly(bytes);
        var source = new InstallationUpdateSource("nexus.installation.update-source", 1, profile, paths.ManagerRoot,
            paths.RegistryKey, InstallerSnapshotCodec.Encode(metadata), bytes);
        Require(source, root, before, profile);
        return _blobs.PersistPlan(JsonSerializer.SerializeToUtf8Bytes(source, Options));
    }

    internal InstallationUpdateSource Read(BlobReference reference, string root, ArchivedFileTree before, string? profile)
    {
        if (reference.SizeBytes is < 1 or > 1048576 || reference.RelativePath != "blobs/plans/" + reference.Sha256 + ".json") throw Invalid();
        using var held = _blobs.OpenVerified(reference);
        var bytes = new byte[checked((int)held.Length)]; held.ReadExactly(bytes);
        using var document = StrictJson.Parse(bytes, 1048576, 524288);
        var source = document.Deserialize<InstallationUpdateSource>(Options) ?? throw Invalid();
        Require(source, root, before, profile);
        return source;
    }

    internal void RequireCurrent(InstallationUpdateSource source, string root, bool checkpoint)
    {
        var paths = Paths(source);
        RequireMembers(paths, checkpoint);
        var before = InstallerSnapshotCodec.Decode(source.Metadata, root);
        if (!InstallerMetadataInventory.Equal(before, InstallerMetadataInventory.CaptureFrozen(before, paths))) throw Invalid();
        var identity = InstallationIdentityCodec.Unseal(source.Installation, root);
        var runtime = new InstanceRoot(contracts).OpenExisting(instance.RuntimeRoot);
        if (runtime.InstanceId != identity.RuntimeId || PhysicalObjectIdentity.Read(runtime.RuntimeRoot) != identity.RuntimeIdentity)
            throw Invalid();
    }

    internal static InstallationIdentity Identity(InstallationUpdateSource source, string root)
        => InstallationIdentityCodec.Unseal(source.Installation, root);

    internal static InstallerPaths Paths(InstallationUpdateSource source)
    {
        var paths = CurrentPaths() ?? throw Invalid();
        if (paths.ManagerRoot != source.ManagerRoot || paths.RegistryKey != source.RegistryKey) throw Invalid();
        return paths;
    }

    internal static void RequireUnowned(string root)
    {
        var paths = CurrentPaths();
        if (paths is not null && Registered(paths, root)) throw Invalid();
    }

    internal static void RequireMembers(InstallerPaths paths, bool checkpoint)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(paths.ManagerRoot))
            if (Path.GetFileName(path) is not (InstallationAnchor.IdentityFile or InstallationAnchor.WorkerFile) &&
                !(checkpoint && Path.GetFileName(path) == "metadata-checkpoint" && Directory.Exists(path))) throw Invalid();
    }

    private void Require(InstallationUpdateSource source, string root, ArchivedFileTree before, string? profile)
    {
        if (source.Format != "nexus.installation.update-source" || source.Version != 1 || profile is not null && source.Profile != profile ||
            source.Profile is not ("test" or "production") || source.Installation is null || source.Installation.Length is < 1 or > 65536 ||
            source.Metadata is null) throw Invalid();
        _ = Paths(source);
        var metadata = InstallerSnapshotCodec.Decode(source.Metadata, root);
        var identity = Identity(source, root);
        var manifest = before.Snapshot.Entries.SingleOrDefault(entry => entry.RelativePath == ApplicationTree.ManifestPath);
        if (identity.State != "active" || identity.RuntimeId != instance.InstanceId || identity.RootIdentity != before.OriginalIdentity ||
            identity.ApplicationRoot != root || manifest?.Sha256 != identity.ManifestSha256 || !metadata.Ownership.Exists ||
            metadata.RootIdentity != identity.RootIdentity) throw Invalid();
        InstallerRegistration.RequireNative(metadata, identity.ProductVersion);
        var seal = metadata.Files.SingleOrDefault(file => file.Role == "manager" && file.Name == InstallationAnchor.IdentityFile);
        var worker = metadata.Files.SingleOrDefault(file => file.Role == "manager" && file.Name == InstallationAnchor.WorkerFile);
        var engine = metadata.Files.SingleOrDefault(file => file.Role == "uninstall" && file.Name == "unins000.exe");
        if (seal is null || seal.SizeBytes != source.Installation.LongLength ||
            seal.Sha256 != Convert.ToHexStringLower(SHA256.HashData(source.Installation)) ||
            worker is null || new InstallerFile(worker.SizeBytes, worker.Sha256, worker.Identity) != identity.Worker ||
            engine is null || new InstallerFile(engine.SizeBytes, engine.Sha256, engine.Identity) != identity.NativeEngine)
            throw Invalid();
        var values = metadata.Ownership.Values;
        if (values.Length != 3 || values.SingleOrDefault(value => value.Name == "IdentitySha256") is not { Kind: RegistryValueKind.String, Text: { } digest } ||
            digest != seal.Sha256 || values.SingleOrDefault(value => value.Name == "ApplicationRoot") is not { Kind: RegistryValueKind.String, Text: { } location } ||
            location != root || values.SingleOrDefault(value => value.Name == "InstallationId") is not { Kind: RegistryValueKind.String, Text: { } installation } ||
            installation != identity.InstallationId.ToString("D")) throw Invalid();
    }

    private static bool Registered(InstallerPaths paths, string root)
    {
        using var key = Registry.CurrentUser.OpenSubKey(paths.RegistryKey, writable: false);
        return key?.GetValue("ApplicationRoot", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string registered &&
            StringComparer.OrdinalIgnoreCase.Equals(root, registered);
    }

    private static InstallerPaths? CurrentPaths()
    {
#if NEXUS_TEST_HOST
        return InstallerTestPaths.TryCurrent();
#else
        return InstallerPaths.Current();
#endif
    }

    internal static IOException Invalid() => new("maintenance_installation_unverified");
}
