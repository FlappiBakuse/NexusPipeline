using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

public sealed record ExtensionDetails(string ExtensionId, long InstalledRevision, string PackageVersion,
    JsonElement Manifest, IReadOnlyDictionary<string, string> Documents, string ArtifactSha256)
{
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> DiagnosticLocales { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();
}

public sealed class ExtensionDetailsReader(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    ContractReader contracts, PayloadApplyRoots roots)
{
    public (byte[] Bytes, string ContentType)? ReadIcon(string extensionId)
    {
        lease.VerifyHeld(instance);
        NexusPipeline.Core.Extensions.ExtensionIdentifiers.Require(extensionId);
        var installed = new ExtensionInstalledEvidence(instance, database, contracts, roots).ReadCurrent(extensionId);
        if (installed?.Metadata.IsInstalled != true) return null;
        using var held = TreeReadLease.AcquireMetadata(installed.Footprint.TargetPath, installed.Footprint.ParentIdentity, installed.Footprint.Payload);
        var manifest = ReadHeldListing(installed, contracts);
        if (manifest.Icon is not { } name) return null;
        var file = installed.Footprint.Payload.Snapshot.Entries.SingleOrDefault(item => item.RelativePath == name);
        if (file is null || file.SizeBytes is < 1 or > 1048576) throw new IOException("extension_icon_invalid");
        var bytes = File.ReadAllBytes(PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(installed.Footprint.TargetPath, name)));
        if (bytes.LongLength != file.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Sha256)
            throw new IOException("extension_icon_changed");
        var png = bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ico = bytes.AsSpan().StartsWith(new byte[] { 0, 0, 1, 0 });
        if (!(name.EndsWith(".png", StringComparison.Ordinal) && png || name.EndsWith(".ico", StringComparison.Ordinal) && ico))
            throw new IOException("extension_icon_format_invalid");
        var current = new ExtensionInstalledEvidence(instance, database, contracts, roots).ReadCurrent(extensionId);
        if (current?.Metadata != installed.Metadata) throw new IOException("extension_installed_evidence_changed");
        return (bytes, png ? "image/png" : "image/x-icon");
    }

    internal static NexusPipeline.Extensibility.Protocol.ExtensionManifest ReadListing(BoundExtensionInstallation installed, ContractReader contracts)
    {
        using var held = TreeReadLease.AcquireMetadata(installed.Footprint.TargetPath, installed.Footprint.ParentIdentity, installed.Footprint.Payload);
        return ReadHeldListing(installed, contracts);
    }

    internal static NexusPipeline.Extensibility.Protocol.ExtensionManifest ReadArchivedListing(RuntimeInstance instance,
        BoundExtensionInstallation installed, ContractReader contracts)
    {
        var package = installed.Operation.Source.Package ?? throw new IOException("extension_original_package_missing");
        using var input = new ImmutableBlobStore(instance, contracts).OpenVerified(package.Archive);
        using var zip = NexusPipeline.Platform.Extensions.ExtensionPackageArchive.Open(input, CancellationToken.None);
        var bytes = zip.ReadManifest(CancellationToken.None);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != installed.Metadata.Package.ManifestSha256)
            throw new IOException("extension_manifest_changed");
        return new NexusPipeline.Platform.Extensions.ExtensionManifestAdmission(contracts).ReadCurrent(bytes, installed.Metadata.ExtensionId);
    }

    private static NexusPipeline.Extensibility.Protocol.ExtensionManifest ReadHeldListing(BoundExtensionInstallation installed, ContractReader contracts)
    {
        var file = installed.Footprint.Payload.Snapshot.Entries.Single(item => item.RelativePath == "manifest.json");
        if (file.SizeBytes > 262144) throw new IOException("extension_manifest_limit");
        var bytes = File.ReadAllBytes(PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(installed.Footprint.TargetPath, "manifest.json")));
        if (bytes.LongLength != file.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != installed.Metadata.Package.ManifestSha256)
            throw new IOException("extension_manifest_changed");
        return new NexusPipeline.Platform.Extensions.ExtensionManifestAdmission(contracts).ReadCurrent(bytes, installed.Metadata.ExtensionId);
    }

    public ExtensionDetails? Read(string extensionId)
    {
        lease.VerifyHeld(instance);
        if (extensionId.Length is < 1 or > 96 || extensionId.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-')))
            throw new ArgumentException("extension_id_invalid");
        var installed = new ExtensionInstalledEvidence(instance, database, contracts, roots).ReadCurrent(extensionId);
        if (installed is null || !installed.Metadata.IsInstalled) return null;
        using var held = TreeReadLease.AcquireMetadata(installed.Footprint.TargetPath, installed.Footprint.ParentIdentity, installed.Footprint.Payload);
        var manifestBytes = ReadFile("manifest.json", 256 * 1024) ?? throw new IOException("extension_manifest_missing");
        if (Convert.ToHexStringLower(SHA256.HashData(manifestBytes)) != installed.Metadata.Package.ManifestSha256)
            throw new IOException("extension_manifest_changed");
        var admitted = new NexusPipeline.Platform.Extensions.ExtensionManifestAdmission(contracts).ReadCurrent(manifestBytes, extensionId);
        using var manifest = StrictJson.Parse(manifestBytes);
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "README.md", "README.en.md", "README.zh-CN.md", "CHANGELOG.md", "CHANGELOG.en.md", "CHANGELOG.zh-CN.md" })
            if (ReadFile(name, 512 * 1024) is { } bytes) documents.Add(name, new UTF8Encoding(false, true).GetString(bytes));
        var diagnostics = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        if (admitted.Localization is { } localization)
            foreach (var (locale, path) in localization.Locales)
            {
                using var dictionary = StrictJson.Parse(ReadFile(path.GetString()!, 262144) ?? throw new IOException("extension_dictionary_missing"));
                var entries = dictionary.RootElement.GetProperty("diagnostics").EnumerateObject().ToArray();
                if (entries.Length > 4096 || entries.Any(item => item.Name.Length is < 1 or > 120 || item.Name.Any(char.IsControl) ||
                    item.Value.ValueKind != JsonValueKind.String || item.Value.GetString() is not { Length: > 0 and <= 2048 }))
                    throw new IOException("extension_dictionary_invalid");
                diagnostics.Add(locale, entries.ToDictionary(item => item.Name, item => item.Value.GetString()!, StringComparer.Ordinal));
            }
        var after = new ExtensionInstalledEvidence(instance, database, contracts, roots).ReadCurrent(extensionId);
        if (after?.Metadata != installed.Metadata) throw new IOException("extension_installed_evidence_changed");
        return new(extensionId, installed.Metadata.Revision, installed.Metadata.Package.PackageVersion,
            manifest.RootElement.Clone(), documents, installed.Metadata.Package.ArchiveSha256) { DiagnosticLocales = diagnostics };

        byte[]? ReadFile(string name, int limit)
        {
            var file = installed.Footprint.Payload.Snapshot.Entries.SingleOrDefault(item => item.RelativePath == name);
            if (file is null) return null;
            if (file.SizeBytes > limit) throw new IOException("extension_document_limit");
            var bytes = File.ReadAllBytes(PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(installed.Footprint.TargetPath, name)));
            if (bytes.LongLength != file.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Sha256)
                throw new IOException("extension_document_changed");
            return bytes;
        }
    }
}
