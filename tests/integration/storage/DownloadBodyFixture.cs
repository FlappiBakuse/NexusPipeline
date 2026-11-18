using System.Net;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class DownloadBodyFixture(byte[] bytes, string runtimeRoot, string fault, CancellationTokenSource cancellation)
{
    private readonly byte[] _bytes = bytes;
    private readonly string _fault = fault;
    internal string? ArchivePath { get; private set; }
    internal bool ReadComplete { get; private set; }
    internal bool ReplacementBlocked { get; private set; }
    private readonly byte[] _unknown = [17, 21, 9];
    internal HttpContent Content() => new BodyContent(this);

    private void Read(int ordinal)
    {
        if (ordinal == 1)
        {
            ArchivePath = Directory.EnumerateFiles(Path.Combine(runtimeRoot, "staging"), "payload.zip", SearchOption.AllDirectories).Single();
            if (_fault == "replace")
            {
                var identity = PhysicalObjectIdentity.Read(ArchivePath);
                Assert.Throws<IOException>(() => File.Move(ArchivePath, ArchivePath + ".replaced"));
                Assert.Throws<IOException>(() => File.WriteAllBytes(ArchivePath, _unknown));
                Assert.Equal(identity, PhysicalObjectIdentity.Read(ArchivePath)); ReplacementBlocked = true;
            }
            if (_fault == "cleanup") File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(ArchivePath)!, "unknown.bin"), _unknown);
        }
        if (ordinal == 2 && _fault is "cancel" or "cleanup") cancellation.Cancel();
    }

    internal void RequireReleased(ResourceLeaseManager resources)
    {
        Assert.NotNull(ArchivePath); Assert.False(File.Exists(ArchivePath));
        var directory = Path.GetDirectoryName(ArchivePath)!;
        if (_fault == "cleanup") Assert.Equal(_unknown, File.ReadAllBytes(Path.Combine(directory, "unknown.bin")));
        else Assert.False(Directory.Exists(directory));
        using var reference = resources.TryAcquire(Guid.NewGuid(),
            PhysicalResourceAdmission.Read(directory, ResourceClaimMode.Reference)).Lease;
        Assert.NotNull(reference);
    }

    private sealed class BodyContent(DownloadBodyFixture owner) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new Body(owner));
        protected override async Task SerializeToStreamAsync(Stream destination, TransportContext? context)
        { using var source = new Body(owner); await source.CopyToAsync(destination); }
    }

    private sealed class Body : MemoryStream
    {
        private readonly DownloadBodyFixture _owner;
        private int _reads;
        internal Body(DownloadBodyFixture owner) : base(Input(owner), writable: false) => _owner = owner;
        private static byte[] Input(DownloadBodyFixture owner)
        {
            var input = owner._fault == "size" ? owner._bytes[..^1] : owner._bytes.ToArray();
            if (owner._fault == "digest") input[^1] ^= 255;
            return input;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _owner.Read(++_reads); cancellationToken.ThrowIfCancellationRequested();
            var read = base.Read(buffer.Span[..Math.Min(buffer.Length, Math.Max(1, checked((int)Length) / 2))]);
            if (read == 0) _owner.ReadComplete = true;
            return ValueTask.FromResult(read);
        }
    }
}
