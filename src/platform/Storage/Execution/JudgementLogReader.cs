namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class JudgementLogReader : IDisposable
{
    private readonly Dictionary<BlobReference, (FileStream Input, PhysicalObjectIdentity Identity)> _held = [];
    private readonly HashSet<(long Offset, long Order, int Source, bool Flush)> _ends = [];
    private RawOutputManifest? _previous;
    private ExecutionOutputDecoder? _decoder;
    private JudgementLogTail _tail = new();
    private bool _continuous = true, _final, _disposed;
    private StableDirectoryLease? _directory;

    internal JudgementLogTail Read(ImmutableBlobStore blobs, RawOutputManifest manifest, bool final)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_continuous || manifest.DiscardedBytes != 0 || manifest.FailureCode is not null) throw Invalid();
        var ends = RawOutputReplay.Ends(manifest);
        var count = 0;
        if (_previous is { } previous)
        {
            if (previous.Binding != manifest.Binding || previous.SegmentId != manifest.SegmentId ||
                previous.FileLogBaseline != manifest.FileLogBaseline || previous.Parts.Length > manifest.Parts.Length ||
                !previous.Parts.SequenceEqual(manifest.Parts.Take(previous.Parts.Length)) ||
                !RawOutputReplay.Ends(previous).All(ends.Contains) || previous.FileSources.Length > manifest.FileSources.Length)
                throw Invalid();
            for (var index = 0; index < previous.FileSources.Length; index++)
            {
                var before = previous.FileSources[index]; var after = manifest.FileSources[index];
                if ((before with { EndOffset = after.EndOffset, EndOrder = after.EndOrder, FlushPartial = after.FlushPartial }) != after ||
                    before.EndOffset is not null && before != after) throw Invalid();
            }
            RequireHeld(blobs);
            if (_final)
            {
                if (!final || previous.Parts.Length != manifest.Parts.Length || !ends.All(_ends.Contains)) throw Invalid();
                return _tail;
            }
            count = previous.Parts.Length;
            if (ends.Any(item => !_ends.Contains(item) && item.Offset < previous.ObservedBytes))
            { _decoder = null; _tail = new(); _ends.Clear(); count = 0; }
        }
        _directory ??= StableDirectoryLease.Acquire(Path.Combine(blobs.Instance.RuntimeRoot, "blobs", "content"));
        _decoder ??= new(manifest.FileLogBaseline is not null, _tail.AppendLine, _ => _continuous = false);
        var pending = ends.Where(item => !_ends.Contains(item)).ToArray(); var next = 0;
        foreach (var part in manifest.Parts.Skip(count))
        {
            var input = Open(blobs, part.Content); input.Position = 0;
            var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            var position = 0;
            var source = part.FileSourceId ?? (part.Source == Processes.ProcessOutputSource.StandardOutput ? -2 : -1);
            while (next < pending.Length && pending[next].Offset <= part.Offset + bytes.Length)
            {
                var end = pending[next++];
                var length = checked((int)Math.Clamp(end.Offset - part.Offset - position, 0, bytes.Length - position));
                _decoder.Write(source, bytes.AsSpan(position, length)); position += length;
                _decoder.Write(end.Source, [], true, end.Flush); _ends.Add(end);
            }
            _decoder.Write(source, bytes.AsSpan(position));
        }
        while (next < pending.Length)
        {
            var end = pending[next++]; _decoder.Write(end.Source, [], true, end.Flush); _ends.Add(end);
        }
        if (final) { _decoder.FlushObservedTails(); _final = true; }
        if (!_continuous) throw Invalid();
        _previous = manifest;
        return _tail;
    }

    private FileStream Open(ImmutableBlobStore blobs, BlobReference content)
    {
        if (_held.TryGetValue(content, out var held)) return held.Input;
        if (_held.Count >= RawOutputCodec.MaximumParts) throw Invalid();
        var input = blobs.OpenVerified(content);
        _held.Add(content, (input, PhysicalObjectIdentity.Read(input.SafeFileHandle)));
        return input;
    }

    private void RequireHeld(ImmutableBlobStore blobs)
    {
        foreach (var (reference, held) in _held)
            if (held.Input.Length != reference.SizeBytes || PhysicalObjectIdentity.Read(held.Input.SafeFileHandle) != held.Identity ||
                PhysicalObjectIdentity.Read(Path.Combine(blobs.Instance.RuntimeRoot, reference.RelativePath)) != held.Identity) throw Invalid();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var held in _held.Values) held.Input.Dispose();
        _held.Clear(); _directory?.Dispose();
    }

    private static IOException Invalid() => new("execution_judgement_log_unverified");
}
