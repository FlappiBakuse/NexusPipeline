using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class BindingRawAllowance
{
    private readonly object _gate = new();
    private readonly long _maximum;
    private long _used;
    private int _parts;

    internal BindingRawAllowance(long maximum, long existingRecorded)
    {
        if (maximum is not (AndroidControlCapacity.MaximumBindingRawBytes or RawOutputCodec.MaximumRecordedBytes) ||
            existingRecorded < 0 || existingRecorded > maximum)
            throw new ArgumentException("execution_binding_raw_allowance_invalid");
        _maximum = maximum; _used = existingRecorded;
    }

    internal bool TryRecordPart()
    {
        lock (_gate)
        {
            if (_parts == RawOutputCodec.MaximumParts) return false;
            _parts++; return true;
        }
    }
    internal bool TryRecord(int bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (_gate)
        {
            if (bytes > _maximum - _used) return false;
            _used += bytes; return true;
        }
    }
    internal void ReleaseUnrecorded(int bytes)
    {
        lock (_gate)
        {
            if (bytes < 0 || bytes > _used) throw new ArgumentOutOfRangeException(nameof(bytes));
            _used -= bytes;
        }
    }
    internal long Used { get { lock (_gate) return _used; } }
}

internal sealed class AndroidObservationAllowance
{
    private readonly object _gate = new();
    private readonly int _maximum;
    private readonly HashSet<(int Pid, long Created)> _identities = [];
    private bool _overflow;

    internal AndroidObservationAllowance(AndroidControlLimits frozen)
    {
        if (frozen.MaximumObservedProcesses != AndroidControlCapacity.MaximumObservedProcesses)
            throw new ArgumentException("execution_android_observation_allowance_invalid");
        _maximum = frozen.MaximumObservedProcesses;
    }

    internal void Observe(ProcessIdentity identity) => Reserve(identity.ProcessId, identity.CreatedAtUtc.ToFileTimeUtc());

    internal void Reserve(int processId, long created)
    {
        if (processId <= 0 || created <= 0) throw new IOException("execution_android_identity_invalid");
        lock (_gate)
        {
            var key = (processId, created);
            if (_identities.Contains(key)) return;
            if (_overflow || _identities.Count >= _maximum) { _overflow = true; throw new IOException("execution_android_observation_limit"); }
            _identities.Add(key);
        }
    }
    internal bool Overflow { get { lock (_gate) return _overflow; } }
}
