using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Extensibility.Scheduling;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

[SupportedOSPlatform("windows")]
internal sealed class WorkRuntimeFacts(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    ScheduledWorkRegistry registry, ContractReader contracts, TimeProvider clock)
{
    internal async ValueTask<WorkTaskSnapshot> ReadAsync(ExtensionScope scope, string providerId, IReadOnlyList<Guid> taskIds,
        CancellationToken cancellation)
    {
        lease.VerifyHeld(instance); cancellation.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(taskIds);
        var ids = taskIds.ToArray();
        if (ids.Length is < 1 or > 128 || ids.Contains(Guid.Empty) || ids.Distinct().Count() != ids.Length)
            throw new ArgumentException("work_task_selection_invalid");
        var entry = Entry(scope, providerId);
        var tasks = new List<Core.Scheduling.ManualWorkTask>();
        foreach (var id in ids)
            tasks.Add((await entry.ReadManualAsync(id, cancellation).ConfigureAwait(false) ?? throw new IOException("manual_work_task_missing")).Task);
        var plans = registry.PublishedPlanningEntries().SingleOrDefault(item => item.ActivationId == entry.ActivationId &&
            item.ExtensionId == entry.ExtensionId && item.ProviderId == entry.ProviderId);
        var publicPlans = plans is null ? null : await plans.ReadPlansAsync(cancellation).ConfigureAwait(false);
        var saved = ReadRuns(entry.ExtensionId, providerId, ids.ToHashSet(), cancellation);
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var now = clock.GetUtcNow(); var after = now;
        var watermark = ScheduleMinuteRows.Checkpoint(connection, transaction);
        if (watermark.HighestUtcMinute is { } highest && highest > now.ToUnixTimeSeconds() / 60)
            after = DateTimeOffset.FromUnixTimeSeconds(checked((highest + 1) * 60)).AddTicks(-1);
        var result = new List<WorkTaskInfo>();
        foreach (var task in tasks)
        {
            cancellation.ThrowIfCancellationRequested();
            DateTimeOffset? next = null;
            foreach (var plan in publicPlans?.Plans.Tasks.Where(plan => plan.OwnerTaskId == task.TaskId) ?? [])
            {
                ManualWorkPolicy.RequirePlan(task, plan);
                var candidate = WorkNextPolicy.Find(plan, after, (date, minute) => WasAccepted(connection, transaction, entry.ExtensionId, providerId, plan.TaskId, date, minute));
                if (candidate is not null && (next is null || candidate < next)) next = candidate;
            }
            var runs = saved.Where(run => run.TaskId == task.TaskId).OrderByDescending(run => run.Ordinal).ToArray();
            result.Add(new(task.TaskId, task.Revision, runs.Any(run => !run.HasStopped), next,
                Array.AsReadOnly(runs.Take(16).Select(run => run.Fact).ToArray())));
        }
        cancellation.ThrowIfCancellationRequested(); entry.Use(() => true);
        return new(new(now, clock.LocalTimeZone.Id), result.AsReadOnly());
    }

    internal bool HasStopped(ExtensionScope scope, string providerId, Guid taskId, Guid runId, Guid attemptId,
        IInstalledWork actual, CancellationToken cancellation)
    {
        lease.VerifyHeld(instance); cancellation.ThrowIfCancellationRequested();
        if (taskId == Guid.Empty || runId == Guid.Empty || attemptId == Guid.Empty || actual.TaskId != taskId || actual.ProviderId != providerId)
            throw new UnauthorizedAccessException("work_writer_query_unbound");
        var entry = Entry(scope, providerId); actual.RequireCurrent();
        if (actual.FrozenInvocation.RunId == runId && actual.FrozenInvocation.AttemptId == attemptId) return false;
        var candidates = ReadRuns(entry.ExtensionId, providerId, new HashSet<Guid> { taskId }, cancellation)
            .Where(run => run.Fact.RunId == runId && run.Fact.AttemptId == attemptId).ToArray();
        if (candidates.Length != 1) return false;
        var saved = candidates[0];
        actual.RequireCurrent(); cancellation.ThrowIfCancellationRequested();
        if (saved.HasStopped) return true;
        // The exact stored prior callback belongs to this task; the live exclusive callback proves its SDK borrows have drained.
        return saved.HasBegun && saved.Creator == JournalWriterStore.CurrentWriter && saved.OwnerGeneration == lease.StartupGeneration;
    }

    private PublishedWorkEntry Entry(ExtensionScope scope, string providerId)
    {
        var entry = registry.RequireManualEntry(scope.Activation.ExtensionId, providerId);
        if (entry.ActivationId != scope.ActivationId || !scope.IsPublished) throw new UnauthorizedAccessException("work_task_activation_unbound");
        return entry;
    }

    internal bool ObserveOwner(string extensionId, string providerId, Guid taskId, Guid operationId, Guid runId,
        Guid attemptId, IInstalledWork actual, CancellationToken cancellation)
    {
        lease.VerifyHeld(instance); actual.RequireCurrent(); cancellation.ThrowIfCancellationRequested();
        var matches = ReadRuns(extensionId, providerId, new HashSet<Guid> { taskId }, cancellation)
            .Where(run => run.Fact.OperationId == operationId && run.Fact.RunId == runId && run.Fact.AttemptId == attemptId).ToArray();
        if (matches.Length != 1 || !matches[0].HasBegun) throw new IOException("work_effect_origin_unverified");
        actual.RequireCurrent(); cancellation.ThrowIfCancellationRequested();
        if (operationId == actual.OperationId) return false;
        var saved = matches[0];
        return saved.HasStopped || actual.TaskId == taskId && actual.ProviderId == providerId &&
            saved.Creator == JournalWriterStore.CurrentWriter && saved.OwnerGeneration == lease.StartupGeneration;
    }

    internal bool ObserveOperation(string extensionId, Guid operationId, Guid runId, Guid attemptId, IInstalledWork actual, CancellationToken cancellation)
    {
        lease.VerifyHeld(instance); actual.RequireCurrent(); cancellation.ThrowIfCancellationRequested();
        var matches = ReadRuns(extensionId, null, null, cancellation)
            .Where(run => run.Fact.OperationId == operationId && run.Fact.RunId == runId && run.Fact.AttemptId == attemptId).ToArray();
        if (matches.Length != 1 || !matches[0].HasBegun) throw new IOException("work_effect_origin_unverified");
        actual.RequireCurrent(); cancellation.ThrowIfCancellationRequested(); if (actual.OperationId == operationId) return false;
        var saved = matches[0];
        return saved.HasStopped || saved.TaskId == actual.TaskId && saved.ProviderId == actual.ProviderId &&
            saved.Creator == JournalWriterStore.CurrentWriter && saved.OwnerGeneration == lease.StartupGeneration;
    }

    private List<RecordedRun> ReadRuns(string extensionId, string? providerId, IReadOnlySet<Guid>? selected, CancellationToken cancellation)
    {
        var result = new List<RecordedRun>();
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        ManualWorkInventory.Visit(instance, database, contracts, state =>
        {
            cancellation.ThrowIfCancellationRequested(); var source = state.Source.Document;
            if (source.ExtensionId != extensionId || providerId is not null && source.ProviderId != providerId || selected is not null && !selected.Contains(source.Task.TaskId)) return;
            var fact = Fact(source.OperationId, source.Invocation, source.Task.Revision, "manual", source.ObservedAtUtc,
                state.Begin is not null, state.Result?.Completion, state.Result?.Returned, state.Result?.ObservedAtUtc, state.Result?.ReasonCode);
            result.Add(new(source.Task.TaskId, source.ProviderId, Ordinal(connection, transaction, source.OperationId), state.Begin is not null,
                Stopped(state.Result is not null, source.Creator), source.Creator, source.OwnerGeneration, fact));
        });
        ScheduledProviderInventory.Visit(instance, database, contracts, state =>
        {
            cancellation.ThrowIfCancellationRequested(); var source = state.Source.Document;
            var taskId = source.ManualTask?.TaskId ?? source.Task.TaskId;
            if (source.ExtensionId != extensionId || providerId is not null && source.ProviderId != providerId || selected is not null && !selected.Contains(taskId)) return;
            var fact = Fact(source.OperationId, source.Invocation, source.Task.Revision, "schedule", source.ObservedAtUtc,
                state.Begin is not null, state.Result?.Completion, state.Result?.Returned, state.Result?.ObservedAtUtc, state.Result?.ReasonCode);
            result.Add(new(taskId, source.ProviderId, Ordinal(connection, transaction, source.OperationId), state.Begin is not null,
                Stopped(state.Result is not null, source.Creator), source.Creator, source.OwnerGeneration, fact));
        });
        return result;
    }

    private static WorkRunFact Fact(Guid operationId, FrozenScheduledInvocation invocation, long revision, string trigger,
        DateTimeOffset acceptedAt, bool begun, string? completion, ScheduledBusinessResult? returned, DateTimeOffset? completedAt, string? reason) =>
        new(operationId, invocation.RunId, invocation.AttemptId, revision, trigger, completion ?? (begun ? "running" : "accepted"),
            acceptedAt, completedAt, returned is null ? null : System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(returned.Business.ToString()), reason);

    private static bool Stopped(bool completed, JournalWriter creator) =>
        completed || Processes.WriterStopEvidence.ObserveProcess(creator.Process) == WriterState.Stopped;

    private static long Ordinal(SqliteConnection connection, SqliteTransaction transaction, Guid operationId)
    {
        using var command = DatabaseConnections.Command(connection, transaction, "SELECT rowid FROM operations WHERE operation_id=$0;", operationId.ToString("D"));
        return command.ExecuteScalar() is long value ? value : throw ManualWorkCodec.Invalid();
    }

    private static bool WasAccepted(SqliteConnection connection, SqliteTransaction transaction, string extensionId, string providerId,
        Guid taskId, DateOnly date, int minute)
    {
        using var command = DatabaseConnections.Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM scheduled_provider_occurrences WHERE extension_id=$0 AND provider_id=$1 AND task_id=$2 AND local_date=$3 AND local_minute=$4);",
            extensionId, providerId, taskId.ToString("D"), date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), minute);
        return (long)command.ExecuteScalar()! != 0;
    }

    private sealed record RecordedRun(Guid TaskId, string ProviderId, long Ordinal, bool HasBegun, bool HasStopped, JournalWriter Creator, Guid OwnerGeneration, WorkRunFact Fact);
}
