using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.Modules.Updates;

internal sealed class UpdateTransportException(string message, Exception? inner = null) : IOException(message, inner);

internal static class UpdateAssetDownloader
{
    internal static async Task<byte[]> ReadAsync(HttpClient http, UpdateSourcePolicy policy, UpdateReleaseAsset asset,
        int limit, CancellationToken token)
    {
        if (asset.Id <= 0 || asset.SizeBytes < 1 || asset.SizeBytes > limit || !UpdateInventory.Hash(asset.Sha256)
            || !Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri)) throw new InvalidDataException("update.asset_identity");
        using var response = await policy.GetAsync(http, uri, UpdateResourceKind.ReleaseAsset, "NexusPipeline-update", token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new UpdateTransportException($"update.asset_http_{(int)response.StatusCode}");
        byte[] bytes = await UpdateJson.ReadBoundedAsync(response.Content, limit, token).ConfigureAwait(false);
        if (bytes.LongLength != asset.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != asset.Sha256)
            throw new InvalidDataException("update.asset_digest_or_size");
        return bytes;
    }

    internal static async Task DownloadAsync(HttpClient http, UpdateSourcePolicy policy, UpdateReleaseAsset asset,
        string destination, IProgress<UpdateDownloadProgress>? progress, CancellationToken token,
        UpdateCreatedArtifacts? artifacts = null)
    {
        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out Uri? uri) || asset.Id <= 0
            || asset.SizeBytes is <= 0 or > UpdateCatalog.MaxDownloadBytes || !UpdateInventory.Hash(asset.Sha256))
            throw new InvalidDataException("update.asset_identity");
        try
        {
            using var response = await policy.GetAsync(http, uri, UpdateResourceKind.ReleaseAsset, "NexusPipeline-update", token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                if (status is 404 or 408 or 429 || status >= 500) throw new UpdateTransportException($"update.asset_http_{status}");
                throw new InvalidDataException($"update.asset_http_{status}");
            }
            if (response.Content.Headers.ContentLength is long length && length != asset.SizeBytes)
                throw new InvalidDataException("update.asset_declared_size");
            PayloadPathSafety.RequireLinkFree(destination);
            if (artifacts is null) Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            else artifacts.EnsureDirectory(Path.GetDirectoryName(destination)!);
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            string hash;
            try
            {
                progress?.Report(new(0, asset.SizeBytes));
                byte[] buffer = new byte[64 * 1024];
                while (true)
                {
                    int read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (written + read > asset.SizeBytes) throw new InvalidDataException("update.asset_actual_size");
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    digest.AppendData(buffer, 0, read);
                    written += read;
                    progress?.Report(new(written, asset.SizeBytes));
                }
            }
            finally
            {
                hash = Convert.ToHexStringLower(digest.GetHashAndReset());
                artifacts?.FileCreated(destination, written, hash);
            }
            if (written != asset.SizeBytes || hash != asset.Sha256)
                throw new InvalidDataException("更新资产 SHA256 或尺寸与服务端记录不符 (update.asset_digest_or_size)");
        }
        catch (HttpRequestException error) { throw new UpdateTransportException("update.asset_transport", error); }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new UpdateTransportException("update.asset_timeout", error); }
    }

    internal static async Task WriteChecksumAsync(string destination, string hash, CancellationToken token, UpdateCreatedArtifacts? artifacts)
    {
        if (!UpdateInventory.Hash(hash)) throw new InvalidDataException("update.checksum_identity");
        PayloadPathSafety.RequireLinkFree(destination);
        byte[] bytes = Encoding.ASCII.GetBytes(hash + Environment.NewLine);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        bool written = false;
        try { await output.WriteAsync(bytes, token).ConfigureAwait(false); written = true; }
        finally { artifacts?.FileCreated(destination, written ? bytes.Length : 0,
            Convert.ToHexStringLower(SHA256.HashData(written ? bytes : []))); }
    }
}
