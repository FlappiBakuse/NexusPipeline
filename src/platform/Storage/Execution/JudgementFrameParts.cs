using System.Collections.Immutable;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class JudgementFrameParts
{
    private const int PartBytes = 1024 * 1024;

    internal static ImmutableArray<BlobReference> Write(ImmutableBlobStore blobs, ReadOnlyMemory<byte> bytes,
        int maximumBytes, Action<BlobReference> hold)
    {
        if (maximumBytes is < 1 or > 32 * 1024 * 1024 || bytes.Length < 1 || bytes.Length > maximumBytes) throw Invalid();
        var parts = ImmutableArray.CreateBuilder<BlobReference>();
        for (var offset = 0; offset < bytes.Length; offset += PartBytes)
        {
            var part = blobs.PersistContent(bytes.Slice(offset, Math.Min(PartBytes, bytes.Length - offset)), default);
            hold(part); parts.Add(part);
        }
        return parts.ToImmutable();
    }

    internal static void Require(long sizeBytes, string sha256, ImmutableArray<BlobReference> parts, int maximumBytes)
    {
        if (maximumBytes is < 1 or > 32 * 1024 * 1024 || sizeBytes < 1 || sizeBytes > maximumBytes ||
            !FileTreePolicy.Digest(sha256) || parts.IsDefaultOrEmpty || parts.Length > (maximumBytes + PartBytes - 1) / PartBytes)
            throw Invalid();
        long sum = 0;
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part is null || part.SizeBytes is < 1 or > PartBytes || !FileTreePolicy.Digest(part.Sha256) ||
                part.RelativePath != $"blobs/content/{part.Sha256}.bin" ||
                index != parts.Length - 1 && part.SizeBytes != PartBytes) throw Invalid();
            sum = checked(sum + part.SizeBytes);
        }
        if (sum != sizeBytes) throw Invalid();
    }

    internal static byte[] Read(ImmutableBlobStore blobs, long sizeBytes, string sha256,
        ImmutableArray<BlobReference> parts, int maximumBytes)
    {
        Require(sizeBytes, sha256, parts, maximumBytes);
        var bytes = new byte[checked((int)sizeBytes)]; var offset = 0;
        foreach (var part in parts)
        {
            using var stream = blobs.OpenVerified(part);
            stream.ReadExactly(bytes.AsSpan(offset, checked((int)part.SizeBytes)));
            offset += checked((int)part.SizeBytes);
        }
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha256) throw Invalid();
        return bytes;
    }

    private static IOException Invalid() => new("execution_judgement_frame_parts_unbound");
}
