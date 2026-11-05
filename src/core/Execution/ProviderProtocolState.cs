namespace NexusPipeline.Core.Execution;

public enum ProviderEventKind { Started, Progress, Terminal }
public sealed record ExecutionProviderSession(Guid InstanceId, Guid ParentOperationId, Guid ParentJournalId,
    Guid RunId, Guid AttemptId, string StepId, string ExtensionId, string EntryId, Guid ActivationId, string PlanDigest, string Nonce);
public sealed record ExecutionProviderSignal(ExecutionProviderSession Identity, long Sequence, ProviderEventKind Kind,
    EngineOutcome? Outcome, string? ReasonCode);
public sealed record ExecutionProviderDecision(EngineOutcome Engine, string ReasonCode, long Sequence);

public sealed class ProviderProtocolState
{
    private readonly object _gate = new();
    private readonly ExecutionProviderSession _identity;
    private long _sequence, _bytes;
    private bool _started, _closed;
    private ExecutionProviderDecision? _terminal;
    public const int MaximumEvents = 10002;
    public const int MaximumEncodedBytes = 2097152;
    public const int TerminalReserveBytes = 16388;
    public ExecutionProviderDecision? Decision { get { lock (_gate) return _terminal; } }

    public ProviderProtocolState(ExecutionProviderSession identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.InstanceId == Guid.Empty || identity.ParentOperationId == Guid.Empty || identity.ParentJournalId == Guid.Empty ||
            identity.RunId == Guid.Empty || identity.AttemptId == Guid.Empty || identity.ActivationId == Guid.Empty ||
            identity.StepId is not { Length: >= 1 and <= 120 } || identity.ExtensionId is not { Length: >= 1 and <= 80 } ||
            identity.EntryId is not { Length: >= 1 and <= 120 } || !Digest(identity.PlanDigest) || !Digest(identity.Nonce))
            throw new ArgumentException("execution_provider_session_invalid");
        _identity = identity with { };
    }

    public bool Observe(ExecutionProviderSignal signal, int encodedBytes)
    {
        ArgumentNullException.ThrowIfNull(signal);
        lock (_gate)
        {
            if (signal.Identity != _identity || signal.Sequence != _sequence + 1 || signal.Sequence > MaximumEvents ||
                !Enum.IsDefined(signal.Kind) || encodedBytes is < 1 or > TerminalReserveBytes ||
                signal.Kind == ProviderEventKind.Started && (_started || signal.Sequence != 1) ||
                signal.Kind != ProviderEventKind.Started && !_started ||
                signal.Kind != ProviderEventKind.Terminal && (signal.Outcome is not null || signal.ReasonCode is not null) ||
                signal.Kind == ProviderEventKind.Terminal && (signal.Outcome is not (EngineOutcome.Succeeded or EngineOutcome.Failed or EngineOutcome.Cancelled) ||
                    !Reason(signal.ReasonCode))) throw new ArgumentException("execution_provider_signal_invalid");
            if (_closed || _terminal is not null) return false;
            var next = checked(_bytes + encodedBytes);
            if (next > MaximumEncodedBytes || signal.Kind != ProviderEventKind.Terminal &&
                (next > MaximumEncodedBytes - TerminalReserveBytes || signal.Sequence >= MaximumEvents))
                throw new InvalidOperationException("execution_provider_transcript_limit");
            _bytes = next; _sequence = signal.Sequence; _started = true;
            if (signal.Kind == ProviderEventKind.Terminal)
                _terminal = new(signal.Outcome!.Value, signal.ReasonCode!, signal.Sequence);
            return true;
        }
    }

    public void CloseAdmission() { lock (_gate) _closed = true; }

    private static bool Digest(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Reason(string? value) => value is { Length: >= 1 and <= 80 } &&
        value[0] is >= 'a' and <= 'z' && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}
