using System.Security.Cryptography;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Automation;

public sealed class AssetPreparationSources : IDisposable
{
    private Dictionary<string, FileStream>? _sources;
    private readonly string _planDigest;

    public AssetPreparationSources(FrozenAssetPreparation frozen, IReadOnlyDictionary<Guid, string> selectedFiles)
    {
        _planDigest = Convert.ToHexStringLower(SHA256.HashData(frozen.Export()));
        var assets = frozen.ReadWire().Assets;
        if (selectedFiles.Count != assets.Count || assets.Any(asset => !selectedFiles.ContainsKey(Guid.Parse(asset.AssetId))))
            throw new IOException("asset_sources_incomplete");
        var held = new Dictionary<string, FileStream>(StringComparer.Ordinal);
        try
        {
            foreach (var asset in assets)
            {
                var path = PhysicalPathPolicy.NormalizeWithoutLinks(selectedFiles[Guid.Parse(asset.AssetId)]);
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    var identity = PhysicalObjectIdentity.Read(file.SafeFileHandle);
                    if (file.Length != asset.Content.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(file)) != asset.Content.Sha256 ||
                        PhysicalObjectIdentity.Read(path) != identity) throw new IOException("asset_source_changed");
                    file.Position = 0;
                    if (!held.TryAdd(asset.Content.Sha256, file)) file.Dispose();
                }
                catch { file.Dispose(); throw; }
            }
            _sources = held;
        }
        catch { foreach (var file in held.Values) file.Dispose(); throw; }
    }

    internal void VerifyBound(FrozenAssetPreparation frozen)
    {
        ObjectDisposedException.ThrowIf(_sources is null, this);
        if (Convert.ToHexStringLower(SHA256.HashData(frozen.Export())) != _planDigest) throw new IOException("asset_sources_plan_unbound");
    }

    internal PhysicalObjectIdentity CopyNew(AssetPreparationDefsAsset asset, string destination, CancellationToken cancellationToken)
    {
        var held = _sources ?? throw new ObjectDisposedException(nameof(AssetPreparationSources));
        if (!held.TryGetValue(asset.Content.Sha256, out var source) || source.Length != asset.Content.SizeBytes || asset.WasPresent)
            throw new IOException("asset_source_unbound");
        source.Position = 0;
        PhysicalPathPolicy.NormalizeWithoutLinks(destination);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long written = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = source.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            written = checked(written + count);
            if (written > asset.Content.SizeBytes) throw new IOException("asset_source_changed");
            hash.AppendData(buffer.AsSpan(0, count));
            output.Write(buffer, 0, count);
        }
        if (written != asset.Content.SizeBytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != asset.Content.Sha256)
            throw new IOException("asset_source_changed");
        output.Flush(flushToDisk: true);
        return PhysicalObjectIdentity.Read(output.SafeFileHandle);
    }

    public void Dispose()
    {
        var held = Interlocked.Exchange(ref _sources, null);
        if (held is null) return;
        foreach (var file in held.Values) file.Dispose();
    }
}
