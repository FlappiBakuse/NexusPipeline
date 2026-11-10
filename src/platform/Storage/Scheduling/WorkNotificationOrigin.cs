using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Scheduling;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal sealed class WorkNotificationOrigin(BlobReference reference, FrozenScheduledInvocation invocation,
    AcceptedExecutionRun activity, ExecutionTimeBudget budget, ResourceLeaseManager resources, ImmutableArray<ResourceClaim> claims,
    string admissionFailure, Action<SqliteConnection, SqliteTransaction> requireCurrent)
{
    internal BlobReference Reference => reference;
    internal Guid OperationId => invocation.OccurrenceId;
    internal AcceptedExecutionRun Activity => activity;
    internal long ReadConsumedMilliseconds() => budget.Read().ConsumedMilliseconds;

    internal T Use<T>(Func<T> action)
    {
        using var root = resources.TryContinueRun(activity, invocation.NotificationOperationId, claims).Lease ??
            throw new IOException(admissionFailure);
        using var held = root.Borrow(resources, invocation.NotificationOperationId, claims);
        return held.Use(action);
    }

    internal void RequireCurrent(SqliteConnection connection, SqliteTransaction transaction) => requireCurrent(connection, transaction);
}
