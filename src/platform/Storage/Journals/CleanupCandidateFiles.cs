using System.Security.Cryptography;

namespace NexusPipeline.Platform.Storage.Journals;

internal static class CleanupCandidateFiles
{
    internal static Guid? SecretId(BlobReference item)
    {
        const string prefix = "secrets/payloads/";
        if (!item.RelativePath.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var suffix = "-" + item.Sha256 + ".bin";
        var name = item.RelativePath[prefix.Length..];
        if (name.Length != 36 + suffix.Length || !name.EndsWith(suffix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[..36], "D", out var id) || id == Guid.Empty || name[..36] != id.ToString("D"))
            throw new IOException("cleanup_secret_path_invalid");
        return id;
    }

    internal static void Validate(BlobReference item)
    {
        var secret = SecretId(item);
        if (item.Sha256.Length != 64 || item.Sha256.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9')) ||
            (secret is null && item.RelativePath != $"blobs/content/{item.Sha256}.bin") ||
            item.SizeBytes < (secret is null ? 0 : 1) || item.SizeBytes > (secret is null ? 1_073_741_824 : 1_048_576))
            throw new IOException("cleanup_content_path_invalid");
    }

    internal static string ResourceKey(BlobReference item)
    {
        Validate(item);
        return SecretId(item) is { } id ? "secret-payload:" + id.ToString("D") : "blob:" + item.Sha256;
    }

    internal static FileStream OpenVerified(RuntimeInstance instance, ImmutableBlobStore blobs, BlobReference item)
    {
        Validate(item);
        // The enclosing recovery may need DELETE access after this candidate inspection closes.
        if (SecretId(item) is null) return blobs.OpenVerified(item, retainInScope: false);
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(instance.RuntimeRoot, item.RelativePath));
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (input.Length != item.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(input)) != item.Sha256)
                throw new IOException("cleanup_secret_bytes_unverified");
            input.Position = 0;
            return input;
        }
        catch { input.Dispose(); throw; }
    }
}
