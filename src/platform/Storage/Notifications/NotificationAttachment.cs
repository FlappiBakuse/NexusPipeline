using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed class NotificationAttachment : IDisposable
{
    private readonly Stream _held;
    internal string FileName { get; }
    internal string ContentType { get; }
    internal long SizeBytes { get; }

    private NotificationAttachment(Stream held, RecordAsset asset, string extension, string contentType)
    { _held = held; FileName = asset.Sha256 + extension; ContentType = contentType; SizeBytes = asset.SizeBytes; }

    internal static NotificationAttachment? Open(RuntimeInstance instance, StateDatabase database, ContractReader contracts, RecordAsset? asset)
    {
        if (asset is null) return null;
        var held = new RecordAssetStore(database, new(instance, contracts)).OpenVerified(asset);
        try
        {
            Span<byte> header = stackalloc byte[12];
            var read = held.Read(header); held.Position = 0;
            if (read >= 8 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                return new(held, asset, ".png", "image/png");
            if (read >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255)
                return new(held, asset, ".jpg", "image/jpeg");
            if (read >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8))
                return new(held, asset, ".webp", "image/webp");
            throw new IOException("notification_attachment_format_unsupported");
        }
        catch { held.Dispose(); throw; }
    }

    internal Stream OpenRead()
    {
        _held.Position = 0;
        return new BorrowedStream(_held);
    }

    public void Dispose() => _held.Dispose();

    private sealed class BorrowedStream(Stream source) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => source.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => source.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => source.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
    }
}
