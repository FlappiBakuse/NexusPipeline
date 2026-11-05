using System.Text.Json;

namespace NexusPipeline.Host.Control.Events;

internal sealed record ControlEventRevision(long Mutation, long Recovery, long Execution = 0, long Interface = 0, string Extensions = "");
internal sealed record ControlEventMessage(long Sequence, string Kind, string Frame);

internal sealed class ControlEventFeed(Guid instanceId, Func<ControlEventRevision> readRevision, Func<object> readSnapshot)
{
    private readonly object _gate = new();
    private readonly Queue<ControlEventMessage> _retained = [];
    private readonly Guid _stream = Guid.NewGuid();
    private long _sequence;
    private ControlEventRevision? _revision;
    private object? _snapshot;

    internal object Snapshot()
    {
        lock (_gate)
        {
            var value = Capture();
            return new { instanceId, streamId = _stream, sequence = _sequence, state = value };
        }
    }

    internal IReadOnlyList<ControlEventMessage> Read(Guid stream, long after)
    {
        lock (_gate)
        {
            try { _ = Capture(); }
            catch (IOException error) when (error.Message == "control_event_snapshot_changed")
            {
                return [Message(_sequence, "resync", new { requiresSnapshot = true })];
            }
            if (stream != _stream || after > _sequence || after < 0 ||
                _retained.Count != 0 && after < _retained.Peek().Sequence - 1)
                return [Message(_sequence, "resync", new { requiresSnapshot = true })];
            return _retained.Where(item => item.Sequence > after).ToArray();
        }
    }

    private object Capture()
    {
        var revision = readRevision();
        if (_revision == revision && _snapshot is not null) return _snapshot;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var value = readSnapshot();
            var current = readRevision();
            if (current != revision) { revision = current; continue; }
            if (JsonSerializer.SerializeToUtf8Bytes(value).Length > 4096) throw new IOException("control_event_snapshot_limit");
            if (_sequence >= 9007199254740991) throw new IOException("control_event_sequence_exhausted");
            _sequence++; _revision = revision; _snapshot = value;
            _retained.Enqueue(Message(_sequence, "state", value));
            while (_retained.Count > 256) _retained.Dequeue();
            return value;
        }
        throw new IOException("control_event_snapshot_changed");
    }

    private ControlEventMessage Message(long sequence, string kind, object data)
    {
        var envelope = new
        {
            format = "nexus.event", version = 1, streamId = _stream, sequence,
            occurredAt = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture), kind, topic = "host.state",
            subject = new { type = "instance", id = instanceId.ToString("D") }, data, instanceId
        };
        return new(sequence, kind, "id: " + _stream.ToString("D") + ":" +
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\ndata: " + JsonSerializer.Serialize(envelope) + "\n\n");
    }
}
