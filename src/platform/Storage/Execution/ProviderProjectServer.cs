using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using NexusPipeline.Extensibility.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class ProviderProjectServer : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly ProviderProjectInput _input;
    private readonly ContractReader _contracts;
    private readonly Action<uint> _requirePeer;
    private readonly CancellationTokenSource _lifetime;
    private readonly Task _receive;
    private bool _disposed;
    private readonly CompiledProjectSnapshot _snapshot;
    private readonly StateDatabase? _database;
    private readonly ImmutableBlobStore _blobs;
    private readonly HashSet<string> _selected;
    private readonly Action<uint>? _requireProtectedPeer;

    internal ProviderProjectServer(ImmutableBlobStore blobs, CompiledProjectSnapshot snapshot, string pipeName,
        ContractReader contracts, Action<uint> requirePeer, CancellationToken lifetime, IReadOnlyList<string> selectedTaskIds,
        IReadOnlyList<string>? executionTaskIds = null, StateDatabase? database = null, Action<uint>? requireProtectedPeer = null,
        IReadOnlyList<TaskDefinitionsRepetitionOffset>? repetitionOffsets = null)
    {
        _snapshot = snapshot; _database = database; _blobs = blobs;
        _requireProtectedPeer = requireProtectedPeer;
        _selected = (executionTaskIds ?? selectedTaskIds).ToHashSet(StringComparer.Ordinal);
        _contracts = contracts; _requirePeer = requirePeer; _input = new(blobs, snapshot, selectedTaskIds, executionTaskIds, repetitionOffsets);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        try
        {
            _pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _receive = ReceiveAsync();
        }
        catch { _input.Dispose(); _lifetime.Dispose(); throw; }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            await _pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
            RequirePeer();
            await ExecutionProviderFrames.WriteAsync(_pipe, new byte[] { 1 }, 1, _lifetime.Token).ConfigureAwait(false);
            long sequence = 0, transferred = 0;
            while (true)
            {
                var bytes = await ExecutionProviderFrames.ReadAsync(_pipe, 65536, _lifetime.Token).ConfigureAwait(false);
                RequirePeer();
                var request = _contracts.Read<ExecutionProjectRequest>(bytes, "execution-project-request.schema.json");
                if (request.Sequence != ++sequence) throw new IOException("execution_project_sequence_invalid");
                if (request.Kind == "secret")
                {
                    await WriteSecretAsync(request).ConfigureAwait(false);
                    continue;
                }
                var response = _input.Read(request);
                if (response.ContentBase64 is { } content)
                {
                    transferred = checked(transferred + Convert.FromBase64String(content).LongLength);
                    if (transferred > 4L * 1024 * 1024 * 1024) throw new IOException("execution_project_transfer_limit");
                }
                var encoded = ExecutionProviderFrames.Encode(response, ExecutionProviderFrames.MaximumBootstrapBytes);
                using var validated = _contracts.Read(encoded, "execution-project-response.schema.json");
                RequirePeer();
                await ExecutionProviderFrames.WriteAsync(_pipe, encoded, ExecutionProviderFrames.MaximumBootstrapBytes, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException or
            System.Text.Json.JsonException or ArgumentException or InvalidOperationException or OverflowException)
        { _pipe.Dispose(); }
    }

    private async Task WriteSecretAsync(ExecutionProjectRequest request)
    {
        if (_database is null || request.Fingerprint != _snapshot.Document.Sha256 || request.Name is null || request.Path is null ||
            request.Index is not null || request.Offset is not null || request.MaximumBytes is not null || !_selected.Contains(request.Path))
            throw new IOException("execution_project_secret_unbound");
        var input = _snapshot.Value.ProtectedInputs.SingleOrDefault(input => input.Key == request.Name &&
            input.OccurrenceIds.Contains(request.Path, StringComparer.Ordinal)) ?? throw new IOException("execution_project_secret_unbound");
        RequirePeer(protectedInput: true);
        using var value = new SecretFieldStore(_blobs.Instance, _database).ReadProjectInput(
            new(_snapshot.Value.Provider.Installed.ExtensionId, _snapshot.Value.BindingId, input.Key), input.Reference);
        byte[]? bytes = null;
        try
        {
            value.Use(text =>
            {
                var count = Encoding.UTF8.GetByteCount(text);
                if (text.Length is < 1 or > 4096 || count > 16384) throw new IOException("execution_project_secret_invalid");
                bytes = new byte[count + 9]; bytes[0] = 3;
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(1, 8), request.Sequence);
                Encoding.UTF8.GetBytes(text, bytes.AsSpan(9));
            });
            RequirePeer(protectedInput: true);
            await ExecutionProviderFrames.WriteAsync(_pipe, bytes!, 16393, _lifetime.Token).ConfigureAwait(false);
        }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    private void RequirePeer(bool protectedInput = false)
    {
        _lifetime.Token.ThrowIfCancellationRequested();
        if (!ClientProcess(_pipe.SafePipeHandle, out var pid)) throw new IOException("execution_project_peer_unbound");
        _requirePeer(pid);
        if (protectedInput) (_requireProtectedPeer ?? throw new IOException("execution_project_secret_owner_required"))(pid);
    }

    internal async Task FinishAsync()
    {
        _lifetime.Cancel(); _pipe.Dispose(); await _receive.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (!_receive.IsCompleted) throw new IOException("execution_project_collection_pending");
        _disposed = true; _input.Dispose(); _pipe.Dispose(); _lifetime.Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientProcess(SafePipeHandle pipe, out uint processId);
}
