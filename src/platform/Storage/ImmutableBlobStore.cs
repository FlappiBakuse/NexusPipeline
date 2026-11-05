using System.Buffers;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage;

public sealed record BlobReference(string RelativePath, long SizeBytes, string Sha256);

public sealed class ImmutableBlobStore
{
    private readonly RuntimeInstance _instance;
    internal RuntimeInstance Instance => _instance;

    public ImmutableBlobStore(RuntimeInstance instance, ContractReader contracts)
    {
        var verified = new InstanceRoot(contracts).OpenExisting(instance.RuntimeRoot);
        if (instance.InstanceId != verified.InstanceId) throw new IOException("runtime_identity_mismatch");
        _instance = verified;
    }

    public BlobReference PersistPlan(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > StrictJson.MaximumBytes) throw new System.Text.Json.JsonException("json_byte_limit");
        var ownedBytes = bytes.ToArray();
        using var document = StrictJson.Parse(ownedBytes);
        var digest = Convert.ToHexStringLower(SHA256.HashData(ownedBytes));
        var reference = new BlobReference($"blobs/plans/{digest}.json", ownedBytes.LongLength, digest);
        using var source = new MemoryStream(ownedBytes, writable: false);
        Persist(source, reference, CancellationToken.None);
        return reference;
    }

    public BlobReference PersistContent(string sourcePath, FileTreeEntry expected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expected.Kind != FileObjectKind.File || expected.SizeBytes < 0 || expected.Sha256 is null)
            throw new ArgumentException("A frozen file entry is required");
        var reference = new BlobReference($"blobs/content/{expected.Sha256}.bin", expected.SizeBytes, expected.Sha256);
        Resolve(reference);
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(sourcePath);
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var identity = PhysicalObjectIdentity.Read(source.SafeFileHandle);
        if (source.Length != expected.SizeBytes)
            throw new IOException("blob_source_changed");
        var copied = Persist(source, reference, cancellationToken);
        if (!copied && Convert.ToHexStringLower(SHA256.HashData(source)) != expected.Sha256 || source.Length != expected.SizeBytes)
            throw new IOException("blob_source_changed");
        if (identity != PhysicalObjectIdentity.Read(path)) throw new IOException("blob_source_identity_changed");
        return reference;
    }

    public BlobReference PersistContent(ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        if (content.Length > StrictJson.MaximumBytes) throw new IOException("configuration_content_limit");
        var bytes = content.ToArray();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var reference = new BlobReference($"blobs/content/{digest}.bin", bytes.LongLength, digest);
            using var input = new MemoryStream(bytes, writable: false);
            Persist(input, reference, cancellationToken);
            return reference;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal void PersistContent(Stream source, BlobReference expected, CancellationToken cancellationToken)
    {
        if (expected.RelativePath != $"blobs/content/{expected.Sha256}.bin")
            throw new ArgumentException("Invalid immutable content reference");
        Persist(source, expected, cancellationToken);
    }

    public void Verify(BlobReference reference)
    {
        using var input = OpenVerified(reference);
    }

    public FileStream OpenVerified(BlobReference reference)
    {
        var path = Resolve(reference, readLeaf: OperatingSystem.IsWindows());
        if (OperatingSystem.IsWindows()) return BlobReadStream.Open(path, reference);
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (input.Length != reference.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(input)) != reference.Sha256)
                throw new IOException("immutable_blob_mismatch");
            input.Position = 0;
            return input;
        }
        catch { input.Dispose(); throw; }
    }

    private bool Persist(Stream source, BlobReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(reference);
        if (File.Exists(path)) { Verify(reference); return false; }
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        using var heldDirectory = StableDirectoryLease.Acquire(directory);
        var staging = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".staging");
        PhysicalObjectIdentity? owned = null;
        try
        {
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                owned = PhysicalObjectIdentity.Read(output.SafeFileHandle);
                WriteStaged(source, output, reference, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (PhysicalObjectIdentity.Read(staging) != owned) throw new IOException("blob_staging_changed");
            try { File.Move(staging, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { Verify(reference); return true; }
            Verify(reference);
            return true;
        }
        finally
        {
            if (owned is not null && File.Exists(staging) && PhysicalObjectIdentity.Read(staging) == owned) File.Delete(staging);
        }
    }

    private static void WriteStaged(Stream source, FileStream output, BlobReference reference, CancellationToken cancellationToken)
    {
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(65536);
            long written = 0;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = source.Read(buffer, 0, buffer.Length);
                    if (count == 0) break;
                    written = checked(written + count);
                    if (written > reference.SizeBytes) throw new IOException("blob_source_changed");
                    hash.AppendData(buffer.AsSpan(0, count));
                    output.Write(buffer, 0, count);
                }
                if (written != reference.SizeBytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != reference.Sha256)
                    throw new IOException("blob_source_changed");
                output.Flush(flushToDisk: true);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        }
    }

    private string Resolve(BlobReference reference, bool readLeaf = false)
    {
        if (reference.SizeBytes is < 0 or > 1_073_741_824 || reference.Sha256.Length != 64 ||
            reference.Sha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            (reference.RelativePath != $"blobs/plans/{reference.Sha256}.json" && reference.RelativePath != $"blobs/content/{reference.Sha256}.bin"))
            throw new ArgumentException("Invalid immutable blob reference");
        var path = Path.Combine(_instance.RuntimeRoot, reference.RelativePath);
        return readLeaf ? PhysicalPathPolicy.NormalizeParent(path) : PhysicalPathPolicy.NormalizeWithoutLinks(path);
    }
}
