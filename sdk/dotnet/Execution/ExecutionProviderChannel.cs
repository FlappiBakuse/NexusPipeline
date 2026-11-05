using System.IO.Pipes;
using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Extensibility.Execution;

/// <summary>Worker-side control channel for one Host-bound invocation, independent of the raw output pipes.</summary>
public sealed class ExecutionProviderChannel : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ExecutionProviderBootstrap _bootstrap;
    private readonly CancellationTokenSource _stopping = new(), _stopReads = new();
    private readonly Task _receive;
    private long _sequence, _recordedBytes;
    private volatile bool _closed;
    private bool _disposed;
    private int _workspaceOpened;
    /// <summary>Cancels on the original Host stop request or loss of its control channel; cleanup must still drain native writers.</summary>
    public CancellationToken Stopping => _stopping.Token;
    /// <summary>The immutable launch input received from the Host; never a business-success authority.</summary>
    public ExecutionProviderBootstrap Bootstrap => JsonSerializer.Deserialize<ExecutionProviderBootstrap>(
        ExecutionProviderFrames.Encode(_bootstrap, ExecutionProviderFrames.MaximumBootstrapBytes))!;
    /// <summary>Borrows the original Host workspace once, verifying its root and separate input/output identities.</summary>
    public ExecutionWorkDirectory OpenWorkspace()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_closed) throw new IOException("execution_provider_channel_closed");
        if (Interlocked.Exchange(ref _workspaceOpened, 1) != 0) throw new IOException("execution_workspace_already_borrowed");
        return new(_bootstrap.Workspace ?? throw new IOException("execution_workspace_missing"), Stopping);
    }

    private ExecutionProviderChannel(NamedPipeClientStream pipe, ExecutionProviderBootstrap bootstrap)
    { _pipe = pipe; _bootstrap = bootstrap; _receive = ReceiveStopAsync(); }

    private async Task ReceiveStopAsync()
    {
        try
        {
            var frame = await ExecutionProviderFrames.ReadAsync(_pipe, 1, _stopReads.Token).ConfigureAwait(false);
            if (frame.Length != 1 || frame[0] != ExecutionProviderFrames.StopRequest)
                throw new IOException("execution_provider_stop_invalid");
            SignalStop();
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        { if (!_stopReads.IsCancellationRequested) SignalStop(); }
    }

    private void SignalStop() { try { _stopping.Cancel(); } catch (AggregateException) { } }

    /// <summary>Reads stdin bootstrap and connects to the exact local control pipe within the caller deadline.</summary>
    public static async Task<ExecutionProviderChannel> ConnectAsync(Stream bootstrapInput, CancellationToken cancellationToken)
    {
        var bytes = await ExecutionProviderFrames.ReadAsync(bootstrapInput, ExecutionProviderFrames.MaximumBootstrapBytes, cancellationToken).ConfigureAwait(false);
        var bootstrap = JsonSerializer.Deserialize<ExecutionProviderBootstrap>(bytes) ?? throw new IOException("execution_provider_bootstrap_missing");
        if (bootstrap.Format != "nexus.execution.provider-bootstrap" || bootstrap.Version != 1 ||
            bootstrap.Identity is null || bootstrap.AttemptOrdinal is < 1 or > 99 ||
            bootstrap.IdempotencyKey is not { Length: 64 } ||
            bootstrap.IdempotencyKey.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            bootstrap.MaximumEvents is < 2 or > 10002 ||
            bootstrap.Identity.Nonce is not { Length: 64 } ||
            bootstrap.PipeName != "NexusExecutionProvider-" + bootstrap.Identity.Nonce ||
            bootstrap.Identity.Nonce.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new IOException("execution_provider_bootstrap_invalid");
        var pipe = new NamedPipeClientStream(".", bootstrap.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var admitted = await ExecutionProviderFrames.ReadAsync(pipe, 1, cancellationToken).ConfigureAwait(false);
            if (admitted.Length != 1 || admitted[0] != 1) throw new IOException("execution_provider_peer_unbound");
            var channel = new ExecutionProviderChannel(pipe, bootstrap);
            await channel.SendAsync("started", null, null, cancellationToken).ConfigureAwait(false);
            return channel;
        }
        catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>Publishes bounded progress; it cannot authorize any native process or business result.</summary>
    public Task PublishProgressAsync(string message, CancellationToken cancellationToken) =>
        SendAsync("progress", null, message, cancellationToken);

    /// <summary>Publishes the first terminal engine claim. The Host still requires real native stopping and output evidence.</summary>
    public Task PublishTerminalAsync(string outcome, string reasonCode, CancellationToken cancellationToken) =>
        SendAsync("terminal", outcome, reasonCode, cancellationToken);

    private async Task SendAsync(string kind, string? outcome, string? text, CancellationToken cancellationToken)
    {
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_closed || _sequence >= _bootstrap.MaximumEvents || kind == "progress" && (_sequence == 0 || _sequence >= _bootstrap.MaximumEvents - 1) ||
                kind == "terminal" && (_sequence == 0 || outcome is not ("succeeded" or "failed" or "cancelled")) ||
                text?.Length > (kind == "progress" ? 256 : 80) ||
                kind == "terminal" && (text is not { Length: > 0 } || text[0] is not (>= 'a' and <= 'z') ||
                    text.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_'))))
                throw new IOException("execution_provider_channel_closed");
            var frame = new ExecutionProviderEvent
            {
                Format = "nexus.execution.provider-event", Version = 1, Identity = _bootstrap.Identity,
                Sequence = _sequence + 1, Kind = kind, Outcome = outcome, Message = kind == "progress" ? text : null,
                ReasonCode = kind == "terminal" ? text : null
            };
            var bytes = ExecutionProviderFrames.Encode(frame, ExecutionProviderFrames.MaximumEventBytes);
            var nextBytes = checked(_recordedBytes + bytes.Length + 4);
            if (nextBytes > ExecutionProviderFrames.MaximumTranscriptBytes ||
                kind != "terminal" && nextBytes > ExecutionProviderFrames.MaximumTranscriptBytes - ExecutionProviderFrames.TerminalReserveBytes)
                throw new IOException("execution_provider_transcript_limit");
            _sequence = frame.Sequence; _recordedBytes = nextBytes;
            if (kind == "terminal") _closed = true;
            try
            {
                await ExecutionProviderFrames.WriteAsync(_pipe, bytes, ExecutionProviderFrames.MaximumEventBytes, cancellationToken).ConfigureAwait(false);
            }
            catch { _closed = true; throw; }
        }
        finally { _write.Release(); }
    }

    /// <summary>Closes the control pipe without manufacturing a terminal response.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _closed = true; _stopReads.Cancel();
        await _pipe.DisposeAsync().ConfigureAwait(false);
        await _receive.ConfigureAwait(false);
        _stopReads.Dispose(); _stopping.Dispose();
    }
}
