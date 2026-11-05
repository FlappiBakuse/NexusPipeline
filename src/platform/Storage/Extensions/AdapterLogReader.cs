using System.Text;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Storage.Extensions;

internal static class AdapterLogReader
{
    internal static bool Read(ImmutableBlobStore blobs, RawOutputManifest raw,
        Action<IReadOnlyList<TaskLogEvent>, bool> consume)
    {
        var events = new List<TaskLogEvent>();
        var sequences = new Dictionary<int, long>();
        var bytes = 0;
        var continuous = RawOutputReplay.Read(blobs, raw, _ => { }, observeSource: (source, line) =>
        {
            var size = Encoding.UTF8.GetByteCount(line);
            if (events.Count == 256 || bytes + size > 65536)
            {
                consume(events.ToArray(), false); events.Clear(); bytes = 0;
            }
            var epoch = source >= 0 ? raw.FileSources.Single(item => item.Id == source) : null;
            var sequence = sequences.GetValueOrDefault(source) + 1;
            sequences[source] = sequence;
            events.Add(new()
            {
                SourceId = epoch is null ? source == -2 ? "stdout" : "stderr" : "file:" + epoch.DeclarationIndex,
                Epoch = epoch?.Id ?? 0, Sequence = sequence, Text = line
            });
            bytes += size;
        });
        consume(events.ToArray(), continuous && raw.IsComplete);
        return continuous && raw.IsComplete;
    }
}
