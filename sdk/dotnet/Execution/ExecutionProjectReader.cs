using System.IO.Pipes;
using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Extensibility.Secrets;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NexusPipeline.Extensibility.Execution;

/// <summary>Reads only the immutable project granted to the current worker invocation.</summary>
public sealed class ExecutionProjectReader : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly string _fingerprint;
    private readonly SemaphoreSlim _exchange = new(1, 1);
    private long _sequence;
    private bool _closed;

    private ExecutionProjectReader(NamedPipeClientStream pipe, string fingerprint)
    { _pipe = pipe; _fingerprint = fingerprint; }

    /// <summary>Connects to the optional project pipe; the Host verifies the actual connecting process.</summary>
    public static async Task<ExecutionProjectReader> ConnectAsync(ExecutionProviderBootstrap bootstrap, CancellationToken cancellationToken)
    {
        var grant = bootstrap.Project ?? throw new IOException("execution_project_unavailable");
        if (grant.Fingerprint is not { Length: 64 } || grant.Fingerprint.Any(value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            bootstrap.Identity.Nonce is not { Length: 64 } || grant.PipeName != "NexusExecutionProject-" + bootstrap.Identity.Nonce ||
            bootstrap.Identity.Nonce.Any(value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new IOException("execution_project_grant_invalid");
        var pipe = new NamedPipeClientStream(".", grant.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var acknowledgement = await ExecutionProviderFrames.ReadAsync(pipe, 1, cancellationToken).ConfigureAwait(false);
            if (acknowledgement.Length != 1 || acknowledgement[0] != 1) throw new IOException("execution_project_peer_unbound");
            return new(pipe, grant.Fingerprint);
        }
        catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>Returns the frozen execution view, root asset and resource-tree metadata.</summary>
    public Task<ExecutionProjectResponse> ManifestAsync(CancellationToken cancellationToken) =>
        ExchangeAsync(Request("manifest"), cancellationToken);

    /// <summary>Returns at most sixteen asset descriptors at the requested zero-based index.</summary>
    public Task<ExecutionProjectResponse> AssetsAsync(long index, CancellationToken cancellationToken) =>
        ExchangeAsync(Request("assets") with { Index = index }, cancellationToken);

    /// <summary>Returns at most sixteen exact frozen resource-tree entries.</summary>
    public Task<ExecutionProjectResponse> TreeAsync(string name, long index, CancellationToken cancellationToken) =>
        ExchangeAsync(Request("tree") with { Name = name, Index = index }, cancellationToken);

    /// <summary>Reads a bounded asset chunk; offsets address its original immutable bytes.</summary>
    public Task<ExecutionProjectResponse> ReadAssetAsync(string name, long offset, int maximumBytes, CancellationToken cancellationToken) =>
        ExchangeAsync(Request("asset") with { Name = name, Offset = offset, MaximumBytes = maximumBytes }, cancellationToken);

    /// <summary>Reads a file only by declared tree name and exact frozen relative path.</summary>
    public Task<ExecutionProjectResponse> ReadFileAsync(string name, string path, long offset, int maximumBytes, CancellationToken cancellationToken) =>
        ExchangeAsync(Request("file") with { Name = name, Path = path, Offset = offset, MaximumBytes = maximumBytes }, cancellationToken);

    /// <summary>Reads one protected input for an explicitly frozen task use. The caller promptly disposes the returned owner.</summary>
    public async Task<SecretDraft> ReadSecretAsync(string key, string occurrenceId, CancellationToken cancellationToken)
    {
        await _exchange.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? bytes = null; char[]? text = null;
        try
        {
            if (_closed || _sequence >= 100000 || key is not { Length: > 0 and <= 80 } || occurrenceId is not { Length: > 0 and <= 120 })
                throw new IOException("execution_project_secret_invalid");
            var request = Request("secret") with { Sequence = ++_sequence, Name = key, Path = occurrenceId };
            await ExecutionProviderFrames.WriteAsync(_pipe, ExecutionProviderFrames.Encode(request, 65536), 65536, cancellationToken).ConfigureAwait(false);
            bytes = await ExecutionProviderFrames.ReadAsync(_pipe, 16393, cancellationToken).ConfigureAwait(false);
            if (bytes.Length < 10 || bytes[0] != 3 || BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(1, 8)) != request.Sequence)
                throw new IOException("execution_project_secret_unbound");
            text = new UTF8Encoding(false, true).GetChars(bytes, 9, bytes.Length - 9);
            cancellationToken.ThrowIfCancellationRequested();
            if (text.Length is < 1 or > 4096) throw new IOException("execution_project_secret_invalid");
            return new SecretDraft(text);
        }
        catch { _closed = true; await _pipe.DisposeAsync().ConfigureAwait(false); throw; }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            if (text is not null) Array.Clear(text);
            _exchange.Release();
        }
    }

    private ExecutionProjectRequest Request(string kind) => new() { Fingerprint = _fingerprint, Sequence = 1, Kind = kind };

    private async Task<ExecutionProjectResponse> ExchangeAsync(ExecutionProjectRequest request, CancellationToken cancellation)
    {
        await _exchange.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (_closed || _sequence >= 100000 || request.Index is < 0 or > 32768 || request.Offset is < 0 or > 1073741824 ||
                request.MaximumBytes is < 1 or > 131072 || request.Name?.Length is > 120 || request.Path?.Length is > 32767)
                throw new IOException("execution_project_request_invalid");
            request = request with { Sequence = ++_sequence };
            try
            {
                await ExecutionProviderFrames.WriteAsync(_pipe, ExecutionProviderFrames.Encode(request, 65536), 65536, cancellation).ConfigureAwait(false);
                var bytes = await ExecutionProviderFrames.ReadAsync(_pipe, ExecutionProviderFrames.MaximumBootstrapBytes, cancellation).ConfigureAwait(false);
                var response = JsonSerializer.Deserialize<ExecutionProjectResponse>(bytes) ?? throw new IOException("execution_project_response_invalid");
                if (response.Fingerprint != _fingerprint || response.Sequence != request.Sequence || response.Kind != request.Kind ||
                    response.Index != request.Index || response.Offset != request.Offset)
                    throw new IOException("execution_project_response_unbound");
                if (request.Kind is "asset" or "file")
                {
                    var content = Convert.FromBase64String(response.ContentBase64 ?? throw new IOException("execution_project_response_invalid"));
                    if (content.LongLength > request.MaximumBytes || response.SizeBytes < request.Offset ||
                        content.LongLength != Math.Min(request.MaximumBytes!.Value, response.SizeBytes!.Value - request.Offset!.Value) ||
                        response.Sha256 is not { Length: 64 }) throw new IOException("execution_project_response_invalid");
                }
                return response;
            }
            catch { _closed = true; await _pipe.DisposeAsync().ConfigureAwait(false); throw; }
        }
        finally { _exchange.Release(); }
    }

    /// <summary>Closes the read channel; it cannot commit configuration or claim execution success.</summary>
    public ValueTask DisposeAsync() { _closed = true; return _pipe.DisposeAsync(); }
}
