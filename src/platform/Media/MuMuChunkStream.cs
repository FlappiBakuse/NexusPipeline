using System.Security.Cryptography;
using NexusPipeline.Platform.Execution.Judgement;

namespace NexusPipeline.Platform.Media;

internal sealed class MuMuChunkStream(Stream input, long size) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private byte[] _chunk = [];
    private int _offset;
    private long _read;
    private bool _disposed;
    internal string FinishHash()
    {
        if (_read != size || _offset != _chunk.Length) throw MuMuCaptureProtocol.Invalid();
        return Convert.ToHexStringLower(_hash.GetHashAndReset());
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0 || _read == size) return 0;
        if (_offset == _chunk.Length)
        {
            _chunk = await JudgementWorkerFraming.ReadAsync(input, cancellation, MuMuCaptureProtocol.MaximumChunkBytes)
                .ConfigureAwait(false) ?? throw MuMuCaptureProtocol.Invalid();
            _offset = 0;
            if (_chunk.Length != Math.Min(MuMuCaptureProtocol.MaximumChunkBytes, size - _read)) throw MuMuCaptureProtocol.Invalid();
        }
        var count = Math.Min(buffer.Length, _chunk.Length - _offset);
        _chunk.AsMemory(_offset, count).CopyTo(buffer); _hash.AppendData(_chunk.AsSpan(_offset, count));
        _offset += count; _read += count; return count;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => size;
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed) { _disposed = true; _hash.Dispose(); }
        base.Dispose(disposing);
    }
}
