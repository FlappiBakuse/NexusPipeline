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
        if (fault == "cancel")
        {
            Assert.Throws<OperationCanceledException>(() => store.PersistContent(input, requested, cancellation.Token));
            Assert.True(cancellation.IsCancellationRequested);
        }
        else
        {
            var rejection = Assert.Throws<IOException>(() => store.PersistContent(input, requested, cancellation.Token));
            Assert.Equal(fault == "io" ? "injected_stream_failure" : "blob_source_changed", rejection.Message);
        }
        var directory = Path.Combine(root, "blobs", "content");
        if (Directory.Exists(directory)) Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        using var retry = new MemoryStream(bytes, writable: false);
        store.PersistContent(retry, expected, CancellationToken.None);
        var published = Assert.Single(Directory.EnumerateFileSystemEntries(directory));
        Assert.Equal(Path.GetFullPath(Path.Combine(root, expected.RelativePath)), published);
        Assert.Equal(bytes, File.ReadAllBytes(published));
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
        var published = Assert.Single(Directory.EnumerateFileSystemEntries(Path.Combine(root, "blobs", "content")));
        Assert.Equal(Path.GetFullPath(Path.Combine(root, expected.RelativePath)), published);
        Assert.Equal(bytes, File.ReadAllBytes(published));
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
        private int _reads;
        public override int Read(byte[] buffer, int offset, int count)
        {
            _reads++;
            if (_reads == 2 && fault == "io") throw new IOException("injected_stream_failure");
            var read = base.Read(buffer, offset, Math.Min(count, 65536));
            if (_reads == 1) firstRead();
            return read;
        }
    }
}
