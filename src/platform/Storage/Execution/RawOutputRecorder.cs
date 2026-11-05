using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Threading.Channels;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class RawOutputRecorder : IOutputByteSink, IDisposable
{
    private abstract record Work;
    private sealed record Data(long Offset, ProcessOutputSource Source, byte[] Bytes, int? FileId = null, long? FileOffset = null) : Work;
    private sealed record Barrier(Header Header, TaskCompletionSource<BlobReference> Result) : Work;
    private sealed record Header(long Observed, long? OutOffset, long? OutOrder, long? ErrorOffset, long? ErrorOrder,
        string? Failure, bool Final, ImmutableArray<FileLogEpoch> Files, bool FilesComplete);
    private readonly object _gate = new();
    private readonly ImmutableBlobStore _blobs;
    private readonly LiveLogWriter? _live;
    private readonly StepEvidenceBinding _binding;
    private readonly long _maximumRecordedBytes;
    private readonly BindingRawAllowance? _bindingAllowance;
    private readonly Guid _segmentId = Guid.NewGuid();
    private readonly Channel<Work> _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(128)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly TaskCompletionSource<BlobReference> _final = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _observed, _order, _accepted;
    private int _queuedData;
    private Task<BlobReference>? _pendingSnapshot;
    private long? _outOffset, _outOrder, _errorOffset, _errorOrder;
    private bool _outClosed, _errorClosed;
    private string? _failure;
    private Header? _finalHeader;
    private readonly BlobReference? _baseline;
    private readonly List<FileLogEpoch> _files = [];
    private bool _filesComplete;
    private readonly ExecutionOutputDecoder? _decoder;
    private readonly Channel<byte> _changes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false });
    internal ChannelReader<byte> Changes => _changes.Reader;
    internal long JudgementBytes { get { lock (_gate) return _judgementBytes; } }
    private long _judgementBytes;
    private long _fileMetadataBytes;
    private readonly List<FileStream> _held = [];
    internal Task<BlobReference> Completion => _final.Task;

    internal RawOutputRecorder(ImmutableBlobStore blobs, StepEvidenceBinding binding, BlobReference? baseline = null, bool decodeLines = false, long maximumRecordedBytes = RawOutputCodec.MaximumRecordedBytes, BindingRawAllowance? bindingAllowance = null, LiveLogWriter? live = null,
        Action<string>? observeLine = null)
    {
        if (maximumRecordedBytes is < 0 or > RawOutputCodec.MaximumRecordedBytes) throw new ArgumentOutOfRangeException(nameof(maximumRecordedBytes));
        _maximumRecordedBytes = maximumRecordedBytes; _bindingAllowance = bindingAllowance; _live = live;
        _blobs = blobs; _binding = binding; _baseline = baseline; _filesComplete = baseline is null;
        _decoder = !decodeLines ? null : new(baseline is not null, line =>
        {
            // Matching is synchronous and pure; only persistence notifications may coalesce.
            observeLine?.Invoke(line); _changes.Writer.TryWrite(0);
        }, reason => _failure ??= reason);
        _ = Task.Run(RecordAsync);
    }

    public bool TryWrite(ProcessOutputSource source, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (!Enum.IsDefined(source) || bytes.Length is < 1 or > 8192 ||
                (source == ProcessOutputSource.StandardOutput ? _outClosed : _errorClosed))
                throw new ArgumentException("execution_output_frame_invalid");
            return WriteCore(source, bytes, null, null);
        }
    }

    private bool WriteCore(ProcessOutputSource source, ReadOnlySpan<byte> bytes, int? fileId, long? fileOffset)
    {
            var offset = _observed; _order++;
            if (bytes.Length > RawOutputCodec.MaximumObservedBytes - _observed)
            { _failure ??= "execution_output_observed_limit"; return false; }
            _observed += bytes.Length;
            if (_accepted + bytes.Length > _maximumRecordedBytes)
            { _failure ??= "execution_output_record_limit"; return false; }
            // Reserve one bounded queue slot for collection even when the byte producer outruns CAS persistence.
            if (Volatile.Read(ref _queuedData) >= 127)
            { _failure ??= "execution_output_queue_overflow"; return false; }
            if (_bindingAllowance is not null && !_bindingAllowance.TryRecord(bytes.Length))
            { _failure ??= "execution_binding_output_limit"; return false; }
            var copy = bytes.ToArray();
            Interlocked.Increment(ref _queuedData);
            if (!_queue.Writer.TryWrite(new Data(offset, source, copy, fileId, fileOffset)))
            {
                Interlocked.Decrement(ref _queuedData);
                _bindingAllowance?.ReleaseUnrecorded(bytes.Length);
                CryptographicOperations.ZeroMemory(copy);
                _failure ??= "execution_output_queue_overflow"; return false;
            }
            _accepted += bytes.Length;
            _live?.Write(offset, source, bytes, fileId, fileOffset);
            if ((_baseline is not null) == fileId.HasValue) _judgementBytes += bytes.Length;
            _decoder?.Write(fileId ?? (source == ProcessOutputSource.StandardOutput ? -2 : -1), bytes);
            return true;
    }

    internal int OpenFile(FileLogEpoch epoch)
    {
        lock (_gate)
        {
            if (_baseline is null || _filesComplete || _files.Count == RawOutputCodec.MaximumFileEpochs)
                throw new IOException("execution_log_epoch_limit");
            var size = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(epoch with
                { Id = 255, EndOffset = RawOutputCodec.MaximumObservedBytes, EndOrder = RawOutputCodec.MaximumObservedBytes + 514 }).Length + 128;
            if (_fileMetadataBytes + size > Serialization.StrictJson.MaximumBytes - RawOutputCodec.MaximumParts * 320 - 8192)
                throw new IOException("execution_log_metadata_limit");
            _fileMetadataBytes += size;
            var id = _files.Count; _files.Add(epoch with { Id = id }); return id;
        }
    }

    internal bool WriteFile(int id, long offset, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (id < 0 || id >= _files.Count || _files[id].EndOffset is not null || bytes.Length is < 1 or > 8192)
                throw new IOException("execution_log_epoch_closed");
            return WriteCore(ProcessOutputSource.StandardOutput, bytes, id, offset);
        }
    }

    internal void EndFile(int id, bool flushPartial)
    {
        lock (_gate)
        {
            if (_files[id].EndOffset is not null) return;
            _files[id] = _files[id] with { EndOffset = _observed, EndOrder = ++_order, FlushPartial = flushPartial };
            _decoder?.Write(id, [], true, flushPartial);
        }
    }

    internal void CompleteFiles(string? failure = null)
    {
        lock (_gate)
        {
            _failure ??= failure;
            foreach (var file in _files.ToArray()) EndFile(file.Id, failure is null);
            _filesComplete = true; TryComplete();
        }
    }

    internal void FailFiles(string reason) { lock (_gate) _failure ??= reason; }

    public void Complete(ProcessOutputSource source, bool reachedEof)
    {
        lock (_gate)
        {
            if (!Enum.IsDefined(source) || (source == ProcessOutputSource.StandardOutput ? _outClosed : _errorClosed))
                throw new ArgumentException("execution_output_end_invalid");
            _order++;
            if (source == ProcessOutputSource.StandardOutput)
            { _outClosed = true; if (reachedEof) { _outOffset = _observed; _outOrder = _order; } }
            else { _errorClosed = true; if (reachedEof) { _errorOffset = _observed; _errorOrder = _order; } }
            if (!reachedEof) _failure ??= "process_output_read_failed";
            _decoder?.Write(source == ProcessOutputSource.StandardOutput ? -2 : -1, [], true);
            TryComplete();
        }
    }

    internal Task<BlobReference> SnapshotAsync()
    {
        lock (_gate)
        {
            if (_finalHeader is not null) return Completion;
            if (_pendingSnapshot is { IsCompleted: false }) return _pendingSnapshot;
            var result = new TaskCompletionSource<BlobReference>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_queue.Writer.TryWrite(new Barrier(Snapshot(false), result)))
                return Completion;
            return _pendingSnapshot = result.Task;
        }
    }

    private void TryComplete()
    {
        if (!_outClosed || !_errorClosed || !_filesComplete || _finalHeader is not null) return;
        _finalHeader = Snapshot(true); _queue.Writer.TryComplete(); _changes.Writer.TryComplete();
    }

    private Header Snapshot(bool final) => new(_observed, _outOffset, _outOrder, _errorOffset, _errorOrder, _failure, final, _files.ToImmutableArray(), _filesComplete);

    private async Task RecordAsync()
    {
        var parts = ImmutableArray.CreateBuilder<RawOutputPart>();
        var buffer = new byte[65536]; var used = 0; long offset = 0;
        var source = ProcessOutputSource.StandardOutput; int? fileId = null; long? fileOffset = null; string? storageFailure = null;
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (work is Data data)
                {
                    Interlocked.Decrement(ref _queuedData);
                    try
                    {
                        if (storageFailure is not null) continue;
                        if (used != 0 && (data.Source != source || data.FileId != fileId || data.FileOffset != (fileOffset is null ? null : fileOffset + used) ||
                            data.Offset != offset + used || used + data.Bytes.Length > buffer.Length)) Flush();
                        if (storageFailure is not null) continue;
                        if (used == 0) { offset = data.Offset; source = data.Source; fileId = data.FileId; fileOffset = data.FileOffset; }
                        data.Bytes.CopyTo(buffer, used); used += data.Bytes.Length;
                        if (used == buffer.Length) Flush();
                    }
                    finally { CryptographicOperations.ZeroMemory(data.Bytes); }
                }
                else if (work is Barrier barrier)
                {
                    try { Flush(); barrier.Result.SetResult(Persist(barrier.Header)); }
                    catch (Exception error) { barrier.Result.SetException(error); }
                }
            }
            Flush();
            Header header; lock (_gate) header = _finalHeader ?? throw new IOException("execution_output_end_missing");
            var artifact = Persist(header);
            var complete = header.Final && header.OutOffset is not null && header.ErrorOffset is not null &&
                header.FilesComplete && header.Observed == parts.Sum(part => part.Content.SizeBytes) && header.Failure is null && storageFailure is null;
            _live?.Complete(complete, complete ? null : "run_log_original_incomplete");
            _final.SetResult(artifact);
        }
        catch (Exception error)
        {
            lock (_gate) { _failure ??= "execution_output_storage_failed"; _queue.Writer.TryComplete(error); }
            while (_queue.Reader.TryRead(out var pending))
            {
                if (pending is Barrier barrier) barrier.Result.TrySetException(error);
                else if (pending is Data data)
                { Interlocked.Decrement(ref _queuedData); CryptographicOperations.ZeroMemory(data.Bytes); }
            }
            _live?.Complete(false, "run_log_storage_failed");
            _final.TrySetException(error);
            _changes.Writer.TryComplete();
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }

        void Flush()
        {
            if (used == 0) return;
            try
            {
                if (parts.Count == RawOutputCodec.MaximumParts || storageFailure is null && _bindingAllowance?.TryRecordPart() == false)
                    storageFailure ??= "execution_output_part_limit";
                if (storageFailure is null)
                {
                    var reference = _blobs.PersistContent(buffer.AsMemory(0, used), CancellationToken.None);
                    _held.Add(_blobs.OpenVerified(reference));
                    parts.Add(new(offset, source, reference) { FileSourceId = fileId, FileOffset = fileOffset });
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { storageFailure ??= "execution_output_storage_failed"; }
            finally { CryptographicOperations.ZeroMemory(buffer.AsSpan(0, used)); used = 0; }
        }

        BlobReference Persist(Header header)
        {
            var recorded = parts.Sum(part => part.Content.SizeBytes);
            return RawOutputCodec.Write(_blobs, new(RawOutputCodec.Format, 1, _binding, _segmentId,
                header.Final, header.Observed, header.Observed - recorded, header.OutOffset, header.OutOrder,
                header.ErrorOffset, header.ErrorOrder, header.Failure ?? storageFailure, parts.ToImmutable())
                { FileLogBaseline = _baseline, FileSources = header.Files, FilesComplete = header.FilesComplete });
        }
    }

    public void Dispose()
    {
        if (!Completion.IsCompleted) throw new IOException("execution_output_collection_pending");
        foreach (var handle in _held) handle.Dispose();
        _held.Clear(); _live?.Dispose();
    }
}
