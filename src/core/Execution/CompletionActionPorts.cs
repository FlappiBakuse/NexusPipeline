namespace NexusPipeline.Core.Execution;

public enum CompletionEffectPhase { Prepared, Countdown, Entered, Accepted, Cancelling, Cancelled, Failed, Unknown }
public sealed record CompletionActionSnapshot(Guid OperationId, string SubjectId, CompletionActionGroup Frozen,
    CompletionEffectPhase Phase, DateTimeOffset AcceptedAtUtc, DateTimeOffset NotBeforeUtc, string? ReasonCode);

public interface IPreparedCompletionAction : IDisposable
{
    CompletionActionSnapshot Snapshot { get; }
}

public interface ICompletionActionStore
{
    IPreparedCompletionAction? Prepare(Guid operationId, string subject, IReadOnlyList<FrozenCompletionIntent> intents);
    CompletionActionSnapshot? Read(Guid operationId, string subject);
}

public interface ICompletionActionEffects
{
    // The platform accepts only its own reservation, backed by current lifecycle/recovery and local authorization.
    Task ExecuteAsync(IPreparedCompletionAction action, CancellationToken cancellation);
    Task CancelAsync(Guid operationId, string subject, CancellationToken cancellation);
}
