using System.Buffers.Binary;
using System.IO.Compression;

namespace NexusPipeline.StorageTests;

internal sealed record ExtensionZipSpec(string Name, byte[] Bytes, int Attributes = 0);

internal static class SafeZipFixture
{
    internal static byte[] Build(params ExtensionZipSpec[] additional)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var item in new[] { new ExtensionZipSpec("manifest.json", "{}"u8.ToArray()) }.Concat(additional))
            {
                var entry = zip.CreateEntry(item.Name, CompressionLevel.NoCompression); entry.ExternalAttributes = item.Attributes;
                using var output = entry.Open(); output.Write(item.Bytes);
            }
        return bytes.ToArray();
    }

    internal static byte[] BuildCompressed(bool descriptor, byte[] content)
    {
        using var bytes = new MemoryStream();
        using Stream target = descriptor ? new NonSeekableOutput(bytes) : bytes;
        using (var zip = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var item in new[] { new ExtensionZipSpec("manifest.json", "{}"u8.ToArray()), new("data.bin", content) })
            {
                var entry = zip.CreateEntry(item.Name, CompressionLevel.Optimal);
                using var output = entry.Open(); output.Write(item.Bytes);
            }
        return bytes.ToArray();
    }

    private sealed class NonSeekableOutput(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }

    internal static int Central(byte[] bytes)
    {
        for (var index = 0; index + 4 <= bytes.Length; index++)
            if (U32(bytes, index) == 0x02014b50) return index;
        throw new IOException("fixture_central_directory_missing");
    }

    internal static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    internal static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    internal static void W16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
    internal static void W32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    internal static int Data(byte[] bytes) => 30 + U16(bytes, 26) + U16(bytes, 28);

    internal sealed class CancelOnContent(byte[] bytes, CancellationTokenSource stop) : MemoryStream(bytes, writable: false)
    {
        private readonly int _data = Data(bytes);
        internal bool HasReadEntryContent { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var before = Position; var read = base.Read(buffer, offset, count);
            if (before == _data && read > 0) { HasReadEntryContent = true; stop.Cancel(); }
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var before = Position; var read = base.Read(buffer);
            if (before == _data && read > 0) { HasReadEntryContent = true; stop.Cancel(); }
            return read;
        }
    }
}
