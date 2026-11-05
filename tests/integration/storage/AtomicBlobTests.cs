using System.Security.Cryptography;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class AtomicBlobTests
{
    [Theory]
    [InlineData("cancel")]
    [InlineData("io")]
    [InlineData("short")]
    [InlineData("digest")]
    public void RejectsPartialWrites(string fault)
    {
        var (root, store) = Create();
        var bytes = Enumerable.Range(0, 150000).Select(index => (byte)index).ToArray();
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var expected = new BlobReference($"blobs/content/{digest}.bin", bytes.Length, digest);
        var requested = fault == "digest" ? expected with { RelativePath = $"blobs/content/{new string('0', 64)}.bin", Sha256 = new string('0', 64) } : expected;
        using var cancellation = new CancellationTokenSource();
        using var input = new FaultReadStream(fault == "short" ? bytes[..70000] : bytes, fault,
            () => { if (fault == "cancel") cancellation.Cancel(); });
        if (fault == "cancel") Assert.Throws<OperationCanceledException>(() => store.PersistContent(input, requested, cancellation.Token));
        else Assert.Throws<IOException>(() => store.PersistContent(input, requested, cancellation.Token));
        Assert.True(input.Reads > 0);
        Assert.False(File.Exists(Path.Combine(root, requested.RelativePath)));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "blobs/content"), "*.staging"));
        using var retry = new MemoryStream(bytes, writable: false);
        store.PersistContent(retry, expected, CancellationToken.None);
        using var published = store.OpenVerified(expected);
        Assert.Equal(bytes.Length, published.Length);
    }

    [Fact]
    public async Task PublishesOnce()
    {
        var (root, store) = Create();
        var bytes = new byte[150000]; Random.Shared.NextBytes(bytes);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var expected = new BlobReference($"blobs/content/{digest}.bin", bytes.Length, digest);
        using var barrier = new Barrier(2);
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            using var input = new FaultReadStream(bytes, "none", () => Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10))));
            store.PersistContent(input, expected, CancellationToken.None);
        })));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "blobs/content")));
        store.Verify(expected);
    }

    private static (string Root, ImmutableBlobStore Store) Create()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(root);
        return (root, new(instance, contracts));
    }

    private sealed class FaultReadStream(byte[] bytes, string fault, Action firstRead) : MemoryStream(bytes, writable: false)
    {
        internal int Reads { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            if (Reads == 2 && fault == "io") throw new IOException("injected_stream_failure");
            var read = base.Read(buffer, offset, Math.Min(count, 65536));
            if (Reads == 1) firstRead();
            return read;
        }
    }
}
