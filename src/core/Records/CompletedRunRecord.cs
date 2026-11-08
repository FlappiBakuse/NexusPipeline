using NexusPipeline.Core.Execution;

namespace NexusPipeline.Core.Records;

public enum RecordDurationKind { Measured, CheckpointLowerBound, Unknown }
public enum EndTimeKind { MeasuredTerminal, ReconciledTerminalObservation, DeclinedRetryObservation }

public sealed record CompletedRunRecord(Guid AutomationId, Guid? UserId, DateTimeOffset EndedAt,
    long? DurationMilliseconds, AttemptFacts FinalAttempt, RecordDurationKind DurationKind = RecordDurationKind.Measured,
    EndTimeKind EndTimeKind = EndTimeKind.MeasuredTerminal)
{
    public void Validate()
    {
        if (AutomationId == Guid.Empty || UserId == Guid.Empty || EndedAt == default || EndedAt.Offset != TimeSpan.Zero ||
            !Enum.IsDefined(DurationKind) || !Enum.IsDefined(EndTimeKind) ||
            (DurationKind == RecordDurationKind.Unknown) != (DurationMilliseconds is null) ||
            EndTimeKind == EndTimeKind.MeasuredTerminal && DurationKind != RecordDurationKind.Measured ||
            DurationKind == RecordDurationKind.Measured && EndTimeKind != EndTimeKind.MeasuredTerminal ||
            DurationMilliseconds is < 0 or > 31_536_000_000 || FinalAttempt.RunId == Guid.Empty || FinalAttempt.AttemptId == Guid.Empty ||
            FinalAttempt.Ordinal < 1 || !Enum.IsDefined(FinalAttempt.Engine) || !Enum.IsDefined(FinalAttempt.Business) ||
            !Enum.IsDefined(FinalAttempt.Lifecycle) || !Enum.IsDefined(FinalAttempt.Recovery) ||
            FinalAttempt.Lifecycle is LifecycleOutcome.NotStarted or LifecycleOutcome.Running || FinalAttempt.Recovery == RecoveryOutcome.Pending ||
            FinalAttempt.ReasonCode.Length is < 1 or > 80 ||
            FinalAttempt.ReasonCode.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            throw new ArgumentException("completed_record_invalid");
    }
}

public sealed record RecordCursor(DateTimeOffset EndedAt, Guid RunId);
public enum RecordScope { All, Groups }
public sealed record RecordQuery(int Limit = 50, Guid? AutomationId = null, Guid? UserId = null,
    BusinessOutcome? Business = null, RecordCursor? After = null,
    DateTimeOffset? EndedFromInclusive = null, DateTimeOffset? EndedUntilExclusive = null, LifecycleOutcome? Lifecycle = null,
    RecordScope Scope = RecordScope.All)
{
    public void Validate()
    {
        if (Limit is < 1 or > 200 || AutomationId == Guid.Empty || UserId == Guid.Empty || !Enum.IsDefined(Scope) ||
            Scope == RecordScope.Groups && UserId is not null ||
            Business is { } business && !Enum.IsDefined(business) ||
            Lifecycle is { } lifecycle && (!Enum.IsDefined(lifecycle) || lifecycle is LifecycleOutcome.NotStarted or LifecycleOutcome.Running) ||
            After is { } cursor && (cursor.RunId == Guid.Empty || cursor.EndedAt == default || cursor.EndedAt.Offset != TimeSpan.Zero) ||
            EndedFromInclusive is { } from && (from == default || from.Offset != TimeSpan.Zero) ||
            EndedUntilExclusive is { } until && (until == default || until.Offset != TimeSpan.Zero) ||
            EndedFromInclusive is { } start && EndedUntilExclusive is { } end && start >= end)
            throw new ArgumentException("record_query_invalid");
    }
}
public sealed record RecordPage(IReadOnlyList<CompletedRunRecord> Items, RecordCursor? Next);

public interface IRunRecordStore
{
    void AppendCompleted(IReadOnlyList<CompletedRunRecord> records);
    RecordPage ReadPage(RecordQuery query);
}
