using System.Collections.Immutable;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record TargetCommandPart(ProcessOutputSource Source, BlobReference Content);

internal sealed class TargetCommandOutput : IOutputByteSink
{
    internal const int MaximumBytes = 2097152;
    private readonly object _gate = new();
    private readonly List<(ProcessOutputSource Source, byte[] Bytes)> _parts = [];
    private readonly BindingRawAllowance? _binding;
    private int _bytes;
    private bool _stdout, _stderr, _failed;

    internal TargetCommandOutput(BindingRawAllowance? binding = null) => _binding = binding;

    public bool TryWrite(ProcessOutputSource source, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (!Enum.IsDefined(source) || _failed || bytes.Length > MaximumBytes - _bytes || _binding is not null && !_binding.TryRecord(bytes.Length))
            { _failed = true; return false; }
            _parts.Add((source, bytes.ToArray())); _bytes += bytes.Length; return true;
        }
    }

    public void Complete(ProcessOutputSource source, bool reachedEof)
    {
        lock (_gate)
        {
            if (!reachedEof || !Enum.IsDefined(source)) _failed = true;
            else if (source == ProcessOutputSource.StandardOutput) _stdout = true;
            else _stderr = true;
        }
    }

    internal bool IsComplete { get { lock (_gate) return !_failed && _stdout && _stderr; } }

    internal ImmutableArray<TargetCommandPart> Persist(ImmutableBlobStore blobs)
    {
        lock (_gate)
        {
            var result = ImmutableArray.CreateBuilder<TargetCommandPart>();
            foreach (var source in Enum.GetValues<ProcessOutputSource>())
            {
                using var combined = new MemoryStream();
                foreach (var part in _parts.Where(part => part.Source == source)) combined.Write(part.Bytes);
                var bytes = combined.ToArray();
                for (var offset = 0; offset < bytes.Length; offset += 1048576)
                    result.Add(new(source, blobs.PersistContent(bytes.AsMemory(offset, Math.Min(1048576, bytes.Length - offset)), CancellationToken.None)));
            }
            return result.ToImmutable();
        }
    }
}
