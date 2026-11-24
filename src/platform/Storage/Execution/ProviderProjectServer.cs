using Microsoft.Win32.SafeHandles;
using NexusPipeline.Extensibility.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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

internal sealed class ProviderProjectInput : IDisposable
{
    private readonly ImmutableBlobStore _blobs;
    private readonly CompiledProjectSnapshot _snapshot;
    private readonly Dictionary<string, ArchivedFileTree> _trees = new(StringComparer.Ordinal);
    private readonly Dictionary<BlobReference, FileStream> _open = [];
    private readonly FileStream _document;
    private readonly ExecutionProjectResponseSelection? _selection;
    private bool _disposed;

    internal ProviderProjectInput(ImmutableBlobStore blobs, CompiledProjectSnapshot snapshot, IReadOnlyList<string>? selectedTaskIds = null,
        IReadOnlyList<string>? executionTaskIds = null, IReadOnlyList<TaskDefinitionsRepetitionOffset>? repetitionOffsets = null)
    {
        _blobs = blobs; _snapshot = snapshot; _document = blobs.OpenVerified(snapshot.Document);
        try
        {
            if (selectedTaskIds is not null)
            {
                if (selectedTaskIds.Count > 4096 || selectedTaskIds.Any(id => id is not { Length: > 0 and <= 120 } || id.Any(char.IsControl)) ||
                    selectedTaskIds.Distinct(StringComparer.Ordinal).Count() != selectedTaskIds.Count) throw Invalid();
                if (executionTaskIds is not null && (executionTaskIds.Count != selectedTaskIds.Count ||
                    executionTaskIds.Any(id => id is not { Length: > 0 and <= 120 } || id.Any(char.IsControl)) ||
                    executionTaskIds.Distinct(StringComparer.Ordinal).Count() != executionTaskIds.Count)) throw Invalid();
                if (repetitionOffsets is not null && (repetitionOffsets.Count > selectedTaskIds.Count ||
                    repetitionOffsets.Any(item => item is null || !selectedTaskIds.Contains(item.TaskId, StringComparer.Ordinal) ||
                        item.CompletedRepetitions is < 0 or > 99) ||
                    repetitionOffsets.Select(item => item.TaskId).Distinct(StringComparer.Ordinal).Count() != repetitionOffsets.Count)) throw Invalid();
                _selection = new() { ConsumerExtensionId = snapshot.Value.Consumer.Installed.ExtensionId,
                    BindingId = snapshot.Value.BindingId.ToString("D"), SelectedTaskIds = selectedTaskIds.ToArray(), ExecutionTaskIds = executionTaskIds?.ToArray(),
                    RepetitionOffsets = repetitionOffsets?.Select(item => item with { }).ToArray() };
            }
            else if (executionTaskIds is not null || repetitionOffsets is not null) throw Invalid();
            foreach (var tree in snapshot.Value.ResourceTrees)
            {
                _trees.Add(tree.Name, CompiledProjectCodec.ReadArchive(blobs, tree.Archive));
            }
        }
        catch { _document.Dispose(); throw; }
    }

    internal ExecutionProjectResponse Read(ExecutionProjectRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request.Fingerprint != _snapshot.Document.Sha256 || request.Sequence is < 1 or > 100000) throw Invalid();
        var response = new ExecutionProjectResponse { Fingerprint = request.Fingerprint, Sequence = request.Sequence, Kind = request.Kind };
        return request.Kind switch
        {
            "manifest" when request.Name is null && request.Path is null && request.Index is null && request.Offset is null && request.MaximumBytes is null =>
                response with { Selection = _selection is null ? null : _selection with { SelectedTaskIds = _selection.SelectedTaskIds.ToArray(),
                    ExecutionTaskIds = _selection.ExecutionTaskIds?.ToArray(), RepetitionOffsets = _selection.RepetitionOffsets?.Select(item => item with { }).ToArray() },
                    RootIndex = _snapshot.Value.RootIndex, View = _snapshot.Value.ExecutionView.EnumerateObject()
                    .ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal), AssetCount = _snapshot.Value.Assets.Length,
                    Trees = _snapshot.Value.ResourceTrees.Select(tree => new ExecutionProjectResponseTreesItem
                        { Name = tree.Name, Path = tree.Path, EntryCount = _trees[tree.Name].Snapshot.Entries.Count }).ToArray() },
            "assets" when request.Name is null && request.Path is null && request.Offset is null && request.MaximumBytes is null => Assets(request, response),
            "tree" when request.Name is not null && request.Path is null && request.Offset is null && request.MaximumBytes is null => Tree(request, response),
            "asset" when request.Name is not null && request.Path is null && request.Index is null =>
                Chunk(request, response, _snapshot.Value.Assets.SingleOrDefault(item => item.Name == request.Name)?.Content ?? throw Invalid()),
            "file" when request.Name is not null && request.Path is not null && request.Index is null => Chunk(request, response, File(request)),
            _ => throw Invalid()
        };
    }

    private ExecutionProjectResponse Assets(ExecutionProjectRequest request, ExecutionProjectResponse response)
    {
        var index = Index(request, _snapshot.Value.Assets.Length);
        return response with { Index = index, Assets = _snapshot.Value.Assets.Skip(index).Take(16).Select(item => new ExecutionProjectResponseAssetsItem
            { Name = item.Name, Kind = item.Kind.ToString().ToLowerInvariant(), SizeBytes = item.Content.SizeBytes, Sha256 = item.Content.Sha256 }).ToArray() };
    }

    private ExecutionProjectResponse Tree(ExecutionProjectRequest request, ExecutionProjectResponse response)
    {
        if (!_trees.TryGetValue(request.Name!, out var tree)) throw Invalid();
        var index = Index(request, tree.Snapshot.Entries.Count);
        return response with { Index = index, Entries = tree.Snapshot.Entries.Skip(index).Take(16).Select(item => new ExecutionProjectResponseEntriesItem
            { Path = item.RelativePath, Kind = item.Kind.ToString().ToLowerInvariant(), SizeBytes = item.SizeBytes, Sha256 = item.Sha256 }).ToArray() };
    }

    private BlobReference File(ExecutionProjectRequest request)
    {
        if (!_trees.TryGetValue(request.Name!, out var tree)) throw Invalid();
        var file = tree.Snapshot.Entries.SingleOrDefault(item => item.RelativePath == request.Path && item.Kind == Core.Configuration.FileObjectKind.File) ?? throw Invalid();
        return tree.Contents.Single(item => item.SizeBytes == file.SizeBytes && item.Sha256 == file.Sha256);
    }

    private ExecutionProjectResponse Chunk(ExecutionProjectRequest request, ExecutionProjectResponse response, BlobReference content)
    {
        if (request.Offset is null or < 0 || request.Offset > content.SizeBytes || request.MaximumBytes is null or < 1 or > 131072) throw Invalid();
        if (!_open.TryGetValue(content, out var input))
        {
            if (_open.Count == 32)
            { var previous = _open.First(); previous.Value.Dispose(); _open.Remove(previous.Key); }
            input = _blobs.OpenVerified(content); _open.Add(content, input);
        }
        input.Position = request.Offset.Value;
        var bytes = new byte[checked((int)Math.Min(request.MaximumBytes.Value, content.SizeBytes - request.Offset.Value))]; input.ReadExactly(bytes);
        return response with { Offset = request.Offset, SizeBytes = content.SizeBytes, Sha256 = content.Sha256, ContentBase64 = Convert.ToBase64String(bytes) };
    }

    private static int Index(ExecutionProjectRequest request, int count)
    { if (request.Index is null or < 0 || request.Index > count) throw Invalid(); return checked((int)request.Index.Value); }
    private static IOException Invalid() => new("execution_project_request_invalid");
    public void Dispose()
    { if (_disposed) return; _disposed = true; foreach (var input in _open.Values) input.Dispose(); _document.Dispose(); }
}
