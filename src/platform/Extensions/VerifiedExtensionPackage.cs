using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Extensions;

internal sealed record ExtensionPackageBinding(Guid InstanceId, Guid OperationId, ExtensionCatalogIdentity Catalog, string CatalogKeyId,
    ExtensionCatalogEntry Entry, string ArchivePath, PhysicalObjectIdentity ArchiveIdentity, PhysicalObjectIdentity ParentIdentity);

[SupportedOSPlatform("windows")]
internal sealed class VerifiedExtensionPackage : IDisposable
{
    private readonly object _gate = new();
    private readonly ExtensionCatalogStore _catalogs;
    private readonly FileStream _input;
    private readonly ExtensionPackageArchive _archive;
    private readonly List<IDisposable> _held;
    private readonly byte[] _manifest;
    private bool _disposed;
    internal ExtensionPackageBinding Binding { get; }
    internal ExtensionManifest Manifest => JsonSerializer.Deserialize<ExtensionManifest>(_manifest)!;
    internal byte[] ManifestBytes() => _manifest.ToArray();
    internal ImmutableArray<ExtensionPackageFile> Files => _archive.Files;

    private VerifiedExtensionPackage(ExtensionCatalogStore catalogs, ExtensionPackageBinding binding,
        FileStream input, ExtensionPackageArchive archive, byte[] manifest, List<IDisposable> held)
    { _catalogs = catalogs; Binding = binding; _input = input; _archive = archive; _manifest = manifest.ToArray(); _held = held; }

    internal static VerifiedExtensionPackage Prepare(ExtensionCatalogStore catalogs, ExtensionStoreChannel channel,
        Guid operationId, ResourceOperationLease root, string extensionId, string requestedArchive,
        ContractReader contracts, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        using var borrow = catalogs.BorrowPackage(root, operationId);
        return borrow.Use(() => PrepareCore(catalogs, channel, operationId, extensionId, requestedArchive, contracts, cancellation));
    }

    private static VerifiedExtensionPackage PrepareCore(ExtensionCatalogStore catalogs, ExtensionStoreChannel channel,
        Guid operationId, string extensionId, string requestedArchive, ContractReader contracts, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var catalog = catalogs.ReadCached(channel) ?? throw Invalid();
        var entry = catalog.Entries.SingleOrDefault(item => item.ExtensionId == extensionId) ?? throw Invalid();
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(requestedArchive);
        var held = new List<IDisposable>(); ExtensionPackageArchive? archive = null;
        try
        {
            held.Add(StableDirectoryLease.Acquire(Path.GetDirectoryName(path)!));
            var input = ProviderPackageReader.OpenRead(path, held);
            if (input.Length != entry.ArtifactBytes ||
                Convert.ToHexStringLower(SHA256.HashData(input)) != entry.ArtifactSha256) throw Invalid();
            input.Position = 0;
            var binding = new ExtensionPackageBinding(catalogs.Instance.InstanceId, operationId, catalog.Identity, catalog.KeyId, entry,
                path, PhysicalObjectIdentity.Read(input.SafeFileHandle), PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!));
            archive = ExtensionPackageArchive.Open(input, cancellation);
            var manifestBytes = archive.ReadManifest(cancellation);
            var manifest = new ExtensionManifestAdmission(contracts).ReadCurrent(manifestBytes, entry.ExtensionId);
            RequireManifest(entry, manifest, manifestBytes, archive.Files);
            RequireContents(manifest, archive, cancellation);
            var result = new VerifiedExtensionPackage(catalogs, binding, input, archive, manifestBytes, held);
            result.RequireCurrent(cancellation);
            return result;
        }
        catch
        {
            archive?.Dispose();
            for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
            throw;
        }
    }

    internal Candidate CaptureCandidate(ResourceOperationLease root, ImmutableBlobStore blobs, CancellationToken cancellation) =>
        Candidate.Capture(this, root, blobs, cancellation);

    internal sealed class Candidate
    {
        private readonly VerifiedExtensionPackage _package;
        private readonly ArchivedFileTree _contents;
        private Candidate(VerifiedExtensionPackage package, ArchivedFileTree contents)
        { _package = package; _contents = contents; }
        internal ExtensionPackageBinding Binding => _package.Binding;

        internal static Candidate Capture(VerifiedExtensionPackage package, ResourceOperationLease root,
            ImmutableBlobStore blobs, CancellationToken cancellation)
        {
            using var borrow = package._catalogs.BorrowPackage(root, package.Binding.OperationId);
            return borrow.Use(() =>
            {
                lock (package._gate)
                {
                    package.RequireInstance(blobs); package.RequireCurrent(cancellation);
                    var candidate = package._archive.CaptureCandidate(blobs, cancellation);
                    package.RequireCurrent(cancellation);
                    return new Candidate(package, candidate);
                }
            });
        }

        internal T Use<T>(ResourceOperationLease root, ImmutableBlobStore blobs, CancellationToken cancellation,
            Func<ExtensionPackageBinding, ArchivedFileTree, T> use)
        {
            using var borrow = _package._catalogs.BorrowPackage(root, Binding.OperationId);
            return borrow.Use(() =>
            {
                lock (_package._gate)
                {
                    _package.RequireInstance(blobs); _package.RequireCurrent(cancellation);
                    FileTreeArchive.ValidateArchive(_contents);
                    return use(Binding, _contents);
                }
            });
        }
    }

    private void RequireInstance(ImmutableBlobStore blobs)
    {
        if (blobs.Instance.InstanceId != Binding.InstanceId ||
            !StringComparer.OrdinalIgnoreCase.Equals(blobs.Instance.RuntimeRoot, _catalogs.Instance.RuntimeRoot)) throw Invalid();
    }

    private void RequireCurrent(CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellation.ThrowIfCancellationRequested();
        var catalog = _catalogs.ReadCached(Binding.Catalog.Channel) ?? throw Invalid();
        if (catalog.Identity != Binding.Catalog || catalog.KeyId != Binding.CatalogKeyId ||
            catalog.Entries.SingleOrDefault(item => item.ExtensionId == Binding.Entry.ExtensionId) != Binding.Entry ||
            PhysicalObjectIdentity.Read(_input.SafeFileHandle) != Binding.ArchiveIdentity ||
            PhysicalObjectIdentity.Read(Binding.ArchivePath) != Binding.ArchiveIdentity ||
            PhysicalObjectIdentity.Read(Path.GetDirectoryName(Binding.ArchivePath)!) != Binding.ParentIdentity ||
            _input.Length != Binding.Entry.ArtifactBytes) throw Invalid();
        PhysicalObjectIdentity.RequireSingleLink(Binding.ArchivePath);
        cancellation.ThrowIfCancellationRequested();
    }

    internal static void RequireManifest(ExtensionCatalogEntry entry, ExtensionManifest manifest, byte[] original,
        ImmutableArray<ExtensionPackageFile> files)
    {
        if (Convert.ToHexStringLower(SHA256.HashData(original)) != entry.ManifestSha256 ||
            manifest.ExtensionId != entry.ExtensionId || manifest.ArtifactName != entry.ArtifactName ||
            manifest.PackageVersion != entry.PackageVersion || manifest.Kind != entry.Kind ||
            manifest.SdkVersion != entry.SdkVersion || manifest.MinHostVersion != entry.MinHostVersion ||
            ExtensionMetadataReader.Read(manifest.Metadata) != entry.Metadata) throw Invalid();
        if (manifest.Kind == "data-specialized")
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal)
                { "manifest.json", "README.md", "CHANGELOG.md", "CHANGELOG.en.md", manifest.Specialized?.AdapterEntry ?? "" };
            if (manifest.Icon is { } icon) allowed.Add(icon);
            if (manifest.Localization is { } localization)
                foreach (var path in localization.Locales.Values) allowed.Add(path.GetString() ?? throw Invalid());
            if (manifest.Managed is not null || manifest.Execution is not null || manifest.Frontend is not null ||
                manifest.Specialized?.AdapterEntry != "adapter/main.js" || files.Any(file => !allowed.Contains(file.RelativePath))) throw Invalid();
        }
        var activation = manifest.Kind switch
        {
            "managed-code" => manifest.Managed?.EntryAssembly,
            "data-specialized" => manifest.Specialized?.AdapterEntry,
            _ => null
        };
        if (activation is null) throw Invalid();
        PhysicalPathPolicy.ValidateRelativePath(activation);
        RequireFile(activation);
        if (manifest.Specialized is { } adapter) RequireFile(adapter.AdapterEntry);
        if (manifest.Icon is { } iconPath) RequireFile(iconPath);
        if (manifest.Frontend is { } frontend)
        {
            RequireFile(frontend.Entry);
            foreach (var style in frontend.Styles) RequireFile(style);
        }
        if (manifest.Execution is { } execution)
        {
            RequireFile(execution.WorkerEntry);
            if (execution.Launcher is { } launcher) RequireFile(launcher.Entry);
        }
        void RequireFile(string relative)
        {
            PhysicalPathPolicy.ValidateRelativePath(relative);
            if (!files.Any(file => file.RelativePath == relative)) throw Invalid();
        }
    }

    internal static void RequireContents(ExtensionManifest manifest, ExtensionPackageArchive archive, CancellationToken cancellation = default)
    {
        if (manifest.Localization is { } localization)
        {
            var metadata = ExtensionMetadataReader.Read(manifest.Metadata) ?? throw Invalid();
            foreach (var (locale, path) in localization.Locales)
            {
                var name = path.GetString() ?? throw Invalid();
                if (!name.StartsWith("i18n/", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal)) throw Invalid();
                PhysicalPathPolicy.ValidateRelativePath(name);
                var bytes = archive.ReadDocument(name, cancellation) ?? throw Invalid();
                if (bytes.Length > 262144) throw Invalid();
                using var dictionary = StrictJson.Parse(bytes);
                var text = dictionary.RootElement.GetProperty("manifest");
                if (!metadata.Locales.TryGetValue(locale, out var expected) || text.GetProperty("displayName").GetString() != expected.DisplayName ||
                    text.GetProperty("gameName").GetString() != expected.GameName || text.GetProperty("summary").GetString() != expected.Summary) throw Invalid();
            }
        }
        if (manifest.Kind != "data-specialized" && manifest.Specialized is null) return;
        foreach (var file in archive.Files.Where(file => manifest.Kind == "data-specialized"
            ? file.RelativePath.EndsWith(".js", StringComparison.Ordinal) || file.RelativePath.EndsWith(".json", StringComparison.Ordinal)
            : file.RelativePath == manifest.Specialized!.AdapterEntry))
        {
            if (file.SizeBytes > 262144) throw Invalid();
            var content = archive.ReadDocument(file.RelativePath, cancellation) ?? throw Invalid();
            var text = new System.Text.UTF8Encoding(false, true).GetString(content);
            if (file.RelativePath.EndsWith(".json", StringComparison.Ordinal)) { using var json = StrictJson.Parse(content); }
            else _ = Jint.Engine.PrepareScript(text);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _archive.Dispose();
            for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose();
        }
    }

    private static IOException Invalid() => new("extension_package_catalog_unbound");
}
