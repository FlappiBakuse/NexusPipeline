using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Media;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class MediaContentStream(MediaFrameContent content, InstalledExtensionConsumption.Use use) : Stream
{
    private readonly object _gate = new();
    private Task? _dispose;
    private int _active;
    private TaskCompletionSource? _idle;
    public override bool CanRead { get { lock (_gate) return _dispose is null && content.Content.CanRead; } }
    public override bool CanSeek { get { lock (_gate) return _dispose is null && content.Content.CanSeek; } }
    public override bool CanWrite => false;
    public override long Length { get { using var operation = Enter(); return content.Content.Length; } }
    public override long Position
    {
        get { using var operation = Enter(); return content.Content.Position; }
        set { using var operation = Enter(); content.Content.Position = value; }
    }
    public override int Read(byte[] buffer, int offset, int count)
    { using var operation = Enter(); return content.Content.Read(buffer, offset, count); }
    public override int Read(Span<byte> buffer)
    { using var operation = Enter(); return content.Content.Read(buffer); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { using var operation = Enter(); var count = await content.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        use.RequireCurrent(); return count; }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { using var operation = Enter(); var read = await content.Content.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        use.RequireCurrent(); return read; }
    public override long Seek(long offset, SeekOrigin origin)
    { using var operation = Enter(); return content.Content.Seek(offset, origin); }
    public override void Flush() { using var operation = Enter(); }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private IDisposable Enter()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_dispose is not null, this);
            if (_active == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _active++;
        }
        var operation = new Operation(this);
        try { use.RequireCurrent(); return operation; }
        catch { operation.Dispose(); throw; }
    }

    private sealed class Operation(MediaContentStream owner) : IDisposable
    {
        private MediaContentStream? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            lock (current._gate)
            {
                current._active--;
                if (current._active == 0) current._idle!.TrySetResult();
            }
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }
    public override ValueTask DisposeAsync()
    {
        TaskCompletionSource completion; Task idle;
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispose = completion.Task; idle = _idle?.Task ?? Task.CompletedTask;
        }
        _ = DisposeCoreAsync(idle, completion);
        return new(completion.Task);
    }
    private async Task DisposeCoreAsync(Task idle, TaskCompletionSource completion)
    {
        try
        {
            await idle.ConfigureAwait(false);
            await content.DisposeAsync().ConfigureAwait(false);
            use.Dispose(); completion.TrySetResult();
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
        GC.SuppressFinalize(this);
    }
}
