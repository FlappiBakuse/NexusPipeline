using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Execution.Monitoring;

/// <summary>A bounded, acknowledged stream. A failed observer receives exactly the same pending batch.</summary>
internal sealed class TaskLogBuffer
{
    private readonly Queue<TaskLogRecord> _pending = new();
    private readonly Dictionary<string, (int Epoch, long Sequence, string Partial)> _sources = new(StringComparer.Ordinal);
    private TaskLogBatch? _inflight;
    private int _characters;
    private bool _gap;
    internal const int BatchCharacters = 256 * 1024;
    internal bool HasMoreAfter(TaskLogBatch batch) => _pending.Count > batch.Records.Length;

    internal void Append(string sourceId, string text, bool newEpoch = false, bool final = false)
    {
        (int Epoch, long Sequence, string Partial) state = _sources.GetValueOrDefault(sourceId, (0, 0L, ""));
        if (newEpoch)
        {
            if (state.Partial.Length > 0) _gap = true;
            state = (state.Epoch + 1, 0, "");
        }
        string content = state.Partial + text;
        int start = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n') continue;
            Add(new(sourceId, state.Epoch, ++state.Sequence, content[start..i].TrimEnd('\r')));
            start = i + 1;
        }
        state.Partial = content[start..];
        if (final && state.Partial.Length > 0)
        {
            Add(new(sourceId, state.Epoch, ++state.Sequence, state.Partial.TrimEnd('\r')));
            state.Partial = "";
        }
        if (state.Partial.Length > BatchCharacters) { state.Partial = ""; _gap = true; }
        _sources[sourceId] = state;
    }

    private void Add(TaskLogRecord record)
    {
        if (record.Text.Length > BatchCharacters || _characters + record.Text.Length > 4 * BatchCharacters || _pending.Count >= 8192)
        { _gap = true; return; }
        _pending.Enqueue(record); _characters += record.Text.Length;
    }

    internal TaskLogBatch Peek()
    {
        if (_inflight is not null) return TaskProtocolJson.Copy(_inflight);
        var records = new List<TaskLogRecord>();
        int chars = 0;
        foreach (var record in _pending)
        {
            if (chars + record.Text.Length > BatchCharacters || records.Count >= 2048) break;
            records.Add(record); chars += record.Text.Length;
        }
        _inflight = new(records.ToArray(), _gap);
        return TaskProtocolJson.Copy(_inflight);
    }

    internal void Acknowledge(TaskLogBatch batch)
    {
        if (_inflight is null || TaskProtocolJson.Write(_inflight) != TaskProtocolJson.Write(batch))
            throw new InvalidDataException("log cursor acknowledgment mismatch");
        foreach (var record in _inflight.Records) { _pending.Dequeue(); _characters -= record.Text.Length; }
        // A gap stays visible for the remainder of this attempt.
        _inflight = null;
    }
}
