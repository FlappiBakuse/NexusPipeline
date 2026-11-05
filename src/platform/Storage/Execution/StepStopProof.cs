using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record StepStopObservation(string ReasonCode, long ElapsedMilliseconds, long LastOutputElapsedMilliseconds,
    long ObservedBytes, long? BusinessAcceptedElapsedMilliseconds);

internal sealed class StepStopProof
{
    private readonly StepEvidenceBinding _binding;
    private readonly ProcessIdentity _root;
    private readonly StepStopObservation _observation;

    private StepStopProof(StepEvidenceBinding binding, ProcessIdentity root, StepStopObservation observation)
    { _binding = binding; _root = root; _observation = observation; }

    [SupportedOSPlatform("windows")]
    internal static StepStopProof? Capture(StepEvidenceCollector owner) => owner.UseStopObservation(
        (binding, root, observation) => new StepStopProof(binding, root, observation));

    internal StepStopObservation Require(StepEvidenceBinding binding, ProcessIdentity? root,
        RawOutputManifest raw, long? closedElapsed)
    {
        if (_binding != binding || _root != root || !raw.IsFinal || closedElapsed is null ||
            _observation.ElapsedMilliseconds > closedElapsed || _observation.ObservedBytes > raw.ObservedBytes)
            throw new IOException("execution_stop_observation_unbound");
        return _observation;
    }

    internal static string Reason(StepStopCause cause) => cause switch
    {
        StepStopCause.BusinessFailed => "execution_business_failed",
        StepStopCause.SuccessGraceExpired => "execution_success_grace_expired",
        StepStopCause.StallTimeout => "execution_step_stall_timeout",
        StepStopCause.StepTotalTimeout => "execution_step_total_timeout",
        _ => throw new ArgumentException("execution_stop_observation_invalid")
    };
}

internal sealed class ExecutionOutputActivity(IOutputByteSink target) : IOutputByteSink
{
    private readonly object _gate = new();
    private long _lastTimestamp, _bytes;

    public bool TryWrite(ProcessOutputSource source, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (bytes.Length != 0)
            {
                _lastTimestamp = Stopwatch.GetTimestamp();
                _bytes = Math.Min(RawOutputCodec.MaximumObservedBytes, _bytes + bytes.Length);
            }
            return target.TryWrite(source, bytes);
        }
    }

    public void Complete(ProcessOutputSource source, bool eof) => target.Complete(source, eof);

    internal void FileBytes(int count)
    {
        lock (_gate)
        {
            _lastTimestamp = Stopwatch.GetTimestamp(); _bytes = Math.Min(RawOutputCodec.MaximumObservedBytes, _bytes + count);
        }
    }

    internal (TimeSpan Idle, long Bytes) Read(TimeSpan elapsed)
    {
        lock (_gate)
        {
            var idle = _lastTimestamp == 0 ? elapsed : Stopwatch.GetElapsedTime(_lastTimestamp);
            return (idle > elapsed ? elapsed : idle, _bytes);
        }
    }
}
