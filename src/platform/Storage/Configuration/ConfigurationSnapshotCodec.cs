using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed record ConfigurationSnapshotDocument(string Format, int Version, FileTreeSnapshot Tree);

internal sealed class ConfigurationSnapshotCodec(ImmutableBlobStore blobs)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal BlobReference Write(FileTreeSnapshot tree) => blobs.PersistPlan(Serialize(FileTreePolicy.Freeze(tree)));

    internal FileTreeSnapshot Read(BlobReference manifest, List<IDisposable>? held = null)
    {
        if (manifest.SizeBytes is < 1 or > StrictJson.MaximumBytes || manifest.RelativePath != $"blobs/plans/{manifest.Sha256}.json")
            throw new IOException("configuration_snapshot_reference_invalid");
        var input = blobs.OpenVerified(manifest);
        held?.Add(input);
        try
        {
            return BlobReadStream.ReadSnapshot(input, () => Decode(input));
        }
        catch (Exception error) when (error is ConfigurationException or JsonException or ArgumentException)
        { throw new IOException("configuration_snapshot_invalid", error); }
        finally { if (held is null) input.Dispose(); }
    }

    private static FileTreeSnapshot Decode(FileStream input)
    {
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        using var document = StrictJson.Parse(bytes);
        var value = document.Deserialize<ConfigurationSnapshotDocument>(Options);
        if (value is null || value.Format != "nexus.configuration.snapshot" || value.Version != 1)
            throw new IOException("configuration_snapshot_format_unknown");
        var tree = FileTreePolicy.Freeze(value.Tree);
        if (!Serialize(tree).AsSpan().SequenceEqual(bytes)) throw new IOException("configuration_snapshot_encoding_unknown");
        return tree;
    }

    private static byte[] Serialize(FileTreeSnapshot tree) =>
        JsonSerializer.SerializeToUtf8Bytes(new ConfigurationSnapshotDocument("nexus.configuration.snapshot", 1, tree), Options);
}
