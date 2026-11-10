using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal sealed record WorkStartEvidence(BlobReference Reference, long ConsumedMilliseconds);

internal sealed class WorkInvocationEvidence
{
    private readonly FrozenScheduledInvocation _invocation;
    private readonly long _maximumConsumedMilliseconds;
    internal Guid OperationId => _invocation.OccurrenceId;
    internal Guid RunId => _invocation.RunId;
    internal WorkStartEvidence? Begin { get; }
    internal bool IsOpen { get; }

    private WorkInvocationEvidence(FrozenScheduledInvocation invocation, WorkStartEvidence? begin,
        long maximumConsumedMilliseconds, bool isOpen)
    { _invocation = invocation; Begin = begin; _maximumConsumedMilliseconds = maximumConsumedMilliseconds; IsOpen = isOpen; }

    internal static WorkInvocationEvidence ReadScheduled(RuntimeInstance instance, ContractReader contracts,
        BlobReference reference, SqliteConnection connection, SqliteTransaction transaction)
    {
        var state = new ScheduledProviderEvidence(instance, contracts).Read(reference, connection, transaction);
        var document = state.Source.Document;
        var begin = state.BeginReference is { } start && state.Begin is { } saved ? new WorkStartEvidence(start, saved.ConsumedMilliseconds) : null;
        return new(document.Invocation, begin, document.Task.TimeoutSeconds * 1000L, state.Close is null && state.Result is null);
    }

    internal static WorkInvocationEvidence ReadManual(RuntimeInstance instance, ContractReader contracts,
        BlobReference reference, SqliteConnection connection, SqliteTransaction transaction)
    {
        var state = ManualWorkRows.Read(instance, contracts, connection, transaction, reference);
        var document = state.Source.Document;
        var begin = state.BeginReference is { } start && state.Begin is { } saved ? new WorkStartEvidence(start, saved.ConsumedMilliseconds) : null;
        return new(document.Invocation, begin, document.Task.TimeoutSeconds * 1000L, state.Result is null);
    }

    internal bool Binds(BlobReference begin, Guid notificationOperation, long consumedMilliseconds, IEnumerable<Guid> targets) =>
        Begin is { } started && started.Reference == begin && notificationOperation == _invocation.NotificationOperationId &&
        started.ConsumedMilliseconds <= consumedMilliseconds && consumedMilliseconds <= _maximumConsumedMilliseconds &&
        targets.All(_invocation.NotificationTaskIds.Contains);
}
