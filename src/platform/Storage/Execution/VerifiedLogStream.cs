namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class VerifiedLogStream : Stream
{
    private readonly IReadOnlyList<Stream> _parts;
    private readonly long _length;
    private int _index;
    private long _position;
    private bool _disposed;

    internal VerifiedLogStream(IReadOnlyList<Stream> parts)
    {
        if (parts.Count > RawOutputCodec.MaximumParts) throw new IOException("record_log_part_limit");
        _parts = Array.AsReadOnly(parts.ToArray());
        _length = _parts.Sum(part => part.Length);
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length { get { ObjectDisposedException.ThrowIf(_disposed, this); return _length; } }
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (_index < _parts.Count && !buffer.IsEmpty)
        {
            var read = _parts[_index].Read(buffer);
            if (read != 0) { _position += read; return read; }
            _index++;
        }
        return 0;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        while (_index < _parts.Count && !buffer.IsEmpty)
        {
            var read = await _parts[_index].ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read != 0) { _position += read; return read; }
            _index++;
        }
        return 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            foreach (var part in _parts) part.Dispose();
        }
        base.Dispose(disposing);
    }

    public override void Flush() => ObjectDisposedException.ThrowIf(_disposed, this);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
