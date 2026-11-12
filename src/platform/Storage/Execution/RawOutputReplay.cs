namespace NexusPipeline.Platform.Storage.Execution;

internal static class RawOutputReplay
{
    internal static bool Read(ImmutableBlobStore blobs, RawOutputManifest manifest, Action<string> observe, bool includePartial = false,
        Action<int, string>? observeSource = null)
    {
        var continuous = manifest.DiscardedBytes == 0 && manifest.FailureCode is null;
        var decoder = new ExecutionOutputDecoder(manifest.FileLogBaseline is not null, observe, _ => continuous = false, observeSource);
        var ends = Ends(manifest);
        var nextEnd = 0;
        foreach (var part in manifest.Parts)
        {
            using var input = blobs.OpenVerified(part.Content); var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            var position = 0; var source = part.FileSourceId ?? (part.Source == Processes.ProcessOutputSource.StandardOutput ? -2 : -1);
            while (nextEnd < ends.Count && ends[nextEnd].Offset <= part.Offset + bytes.Length)
            {
                var end = ends[nextEnd++];
                var length = checked((int)Math.Clamp(end.Offset - part.Offset - position, 0, bytes.Length - position));
                if (continuous) decoder.Write(source, bytes.AsSpan(position, length)); position += length;
                if (continuous) decoder.Write(end.Source, [], true, end.Flush);
            }
            if (continuous) decoder.Write(source, bytes.AsSpan(position));
        }
        while (nextEnd < ends.Count)
        {
            var end = ends[nextEnd++]; if (continuous) decoder.Write(end.Source, [], true, end.Flush);
        }
        if (continuous && includePartial) decoder.FlushObservedTails();
        return continuous;
    }

    internal static List<(long Offset, long Order, int Source, bool Flush)> Ends(RawOutputManifest manifest)
    {
        var ends = new List<(long Offset, long Order, int Source, bool Flush)>();
        if (manifest.StandardOutputEofOffset is { } stdout) ends.Add((stdout, manifest.StandardOutputEndOrder!.Value, -2, true));
        if (manifest.StandardErrorEofOffset is { } stderr) ends.Add((stderr, manifest.StandardErrorEndOrder!.Value, -1, true));
        foreach (var source in manifest.FileSources)
            if (source.EndOffset is { } end) ends.Add((end, source.EndOrder!.Value, source.Id, source.FlushPartial));
        ends.Sort((left, right) => left.Offset != right.Offset ? left.Offset.CompareTo(right.Offset) : left.Order.CompareTo(right.Order));
        return ends;
    }
}
