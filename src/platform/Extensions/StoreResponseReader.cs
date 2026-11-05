using System.Net;
using System.Security.Cryptography;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Extensions;

internal static class StoreResponseReader
{
    internal const long MaximumArtifactBytes = 256 * 1024 * 1024;

    internal static async Task<byte[]> Metadata(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        RequireResponse(response, StrictJson.MaximumBytes, null);
        using var destination = new MemoryStream();
        await Copy(response, destination, StrictJson.MaximumBytes, null, null, cancellationToken);
        return destination.ToArray();
    }

    internal static async Task Artifact(HttpResponseMessage response, Stream destination, long length,
        string sha256, CancellationToken cancellationToken, long maximumBytes = MaximumArtifactBytes)
    {
        if (maximumBytes is < 1 or > 1073741824 || length < 1 || length > maximumBytes || sha256.Length != 64 ||
            sha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || !destination.CanWrite)
            throw Invalid();
        RequireResponse(response, length, length);
        await Copy(response, destination, length, length, sha256, cancellationToken);
    }

    private static void RequireResponse(HttpResponseMessage response, long maximum, long? expected)
    {
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Count != 0 ||
            response.Content.Headers.ContentRange is not null ||
            response.Content.Headers.ContentLength is { } length &&
                (length < 1 || length > maximum || expected is { } exact && length != exact)) throw Invalid();
    }

    private static async Task Copy(HttpResponseMessage response, Stream destination, long maximum,
        long? expected, string? sha256, CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var digest = sha256 is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (read > maximum - total) throw Invalid();
            digest?.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
        }
        if (total == 0 || expected is { } exact && total != exact ||
            response.Content.Headers.ContentLength is { } declared && total != declared ||
            digest is not null && Convert.ToHexStringLower(digest.GetHashAndReset()) != sha256) throw Invalid();
        await destination.FlushAsync(cancellationToken);
    }

    private static IOException Invalid() => new("extension_store_download_invalid");
}
