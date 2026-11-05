namespace NexusPipeline.Extensibility.Scheduling;

/// <summary>Controls actual Host work; private task metadata still comes from the registered handler.</summary>
public interface IWorkTasks
{
    /// <summary>Reads current Host runtime facts and the next actual public occurrence for up to 128 owned tasks.</summary>
    ValueTask<WorkTaskSnapshot> ReadAsync(string providerId, IReadOnlyList<Guid> taskIds, CancellationToken cancellationToken);

    /// <summary>Requires the current exclusive callback and the exact stored prior run, rather than a plugin cache outcome.</summary>
    ValueTask<bool> HasStoppedAsync(string providerId, Guid taskId, Guid runId, Guid attemptId, CancellationToken cancellationToken);

    /// <summary>Uses the actual Host clock and local zone for public plan metadata.</summary>
    ValueTask<WorkClock> ReadClockAsync(CancellationToken cancellationToken);

    /// <summary>Reads the actual callback with exclusive task ownership after all prior task callbacks and SDK borrows have drained.</summary>
    ValueTask<WorkInvocation> ReadCurrentAsync(CancellationToken cancellationToken);

    /// <summary>Uses the original authenticated frontend write operation; the request cannot choose a run or date.</summary>
    ValueTask<ManualRunReceipt> RunAsync(string providerId, Guid taskId, long expectedRevision, CancellationToken cancellationToken);

    /// <summary>Retains exclusion through the caller's atomic publication; disposal only releases a completed drain.</summary>
    ValueTask<IAsyncDisposable> QuiesceAsync(string providerId, Guid taskId, long expectedRevision, CancellationToken cancellationToken);
}

/// <summary>Acceptance is durable; it does not assert that the callback or any external request succeeded.</summary>
public sealed record ManualRunReceipt(Guid OperationId, Guid RunId, Guid AttemptId, bool IsNew);

/// <summary>The Host freezes the local calendar day when it accepts work, independently of callback delay.</summary>
public sealed record WorkInvocation(Guid RunId, Guid AttemptId, Guid SubjectId, Guid TaskId, long TaskRevision, DateOnly LocalDate, string TimeZoneId, DateTimeOffset DeadlineUtc);

/// <summary>This current clock snapshot does not replace a run's frozen local calendar day.</summary>
public sealed record WorkClock(DateTimeOffset UtcNow, string TimeZoneId);

/// <summary>Completion and business outcome remain separate Host facts.</summary>
public sealed record WorkRunFact(Guid OperationId, Guid RunId, Guid AttemptId, long TaskRevision, string Trigger, string Completion,
    DateTimeOffset AcceptedAtUtc, DateTimeOffset? CompletedAtUtc, string? BusinessOutcome, string? ReasonCode);

/// <summary>Run facts never contain the extension's private per-game result cache.</summary>
public sealed record WorkTaskInfo(Guid TaskId, long Revision, bool IsRunning, DateTimeOffset? NextRunAt, IReadOnlyList<WorkRunFact> Runs);

/// <summary>The clock and all identities come from the actual Host scheduling owner.</summary>
public sealed record WorkTaskSnapshot(WorkClock Clock, IReadOnlyList<WorkTaskInfo> Tasks);
