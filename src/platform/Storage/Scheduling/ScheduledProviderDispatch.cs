using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

[SupportedOSPlatform("windows")]
internal sealed class ScheduledProviderDispatch : IPlannedInvocationAuthorization, IManualInvocationAuthorization
{
    private const int MaximumConcurrent = 4;
    private readonly object _gate = new();
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly TimeProvider _clock;
    private readonly ScheduledProviderPublication _publication;
    private readonly Dictionary<Guid, AcceptedProviderInvocation> _held = [];
    private readonly Dictionary<Guid, Task<ScheduledBusinessResult?>> _tasks = [];
    private readonly CancellationTokenSource _stopping = new();
    private long? _scanned;
    private Task? _stopTask;
    private Exception? _cancellationFailure;
    private ManualWorkRuntime? _manual;
    internal ScheduledWorkRegistry Registry { get; }

    internal ScheduledProviderDispatch(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, ResourceLeaseManager resources, TimeProvider clock)
    {
        _instance = instance; _database = database; _contracts = contracts; _lease = lease; _resources = resources; _clock = clock;
        _publication = new(instance, database, contracts, lease, resources, clock); Registry = new(this);
    }

    internal void BindManual(ManualWorkRuntime manual)
    {
        lock (_gate)
        {
            if (_manual is not null || !manual.UsesRegistry(Registry) || _stopping.IsCancellationRequested)
                throw new InvalidOperationException("manual_work_owner_unbound");
            _manual = manual;
        }
    }

    internal async Task TickAsync(bool canCommit)
    {
        _lease.VerifyHeld(_instance);
        if (_manual?.FailureCode is { } failure) throw new IOException(failure);
        AcceptedProviderInvocation[] measured;
        lock (_gate) measured = _held.Values.ToArray();
        foreach (var work in measured)
            if (work.Budget.Read().IsExhausted) Close(work, "scheduled_provider_deadline");
        await CollectReturnedAsync().ConfigureAwait(false);
        if (_stopping.IsCancellationRequested) return;
        if (canCommit)
        {
            ScheduleMinuteDocument? minute;
            using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                var current = _clock.GetUtcNow().ToUnixTimeSeconds() / 60;
                minute = ScheduleMinuteRows.ReadMinute(connection, transaction, ScheduleMinuteCodec.OperationId(_instance.InstanceId, current), _instance.InstanceId);
            }
            if (minute is not null && (_scanned is null || minute.Scan.HighestUtcMinute > _scanned))
            {
                var scanningMinute = minute.Scan.HighestUtcMinute;
                var retryMinute = false;
                foreach (var entry in Registry.PublishedPlanningEntries())
                {
                    if (_stopping.IsCancellationRequested || _clock.GetUtcNow().ToUnixTimeSeconds() / 60 != scanningMinute) break;
                    var started = _clock.GetTimestamp();
                    using var reading = _resources.TryAcquire(Guid.NewGuid(),
                        [new("extension:" + entry.ExtensionId, ResourceClaimMode.Reference)]).Lease;
                    if (reading is null) { retryMinute = true; continue; }
                    using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                    readDeadline.CancelAfter(TimeSpan.FromSeconds(30));
                    ObservedWorkPlans plans;
                    try { plans = await entry.ReadPlansAsync(readDeadline.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (readDeadline.IsCancellationRequested) { continue; }
                    catch (InvalidOperationException error) when (error.Message is "scheduled_provider_revoked" or "scheduled_plans_unavailable") { continue; }
                    if (_clock.GetUtcNow().ToUnixTimeSeconds() / 60 != scanningMinute) break;
                    foreach (var occurrence in ScheduleMinutePolicy.ProviderOccurrences(_instance.InstanceId, plans, minute.ObservedAtUtc))
                    {
                        lock (_gate) if (_held.Count >= 4096) break;
                        var budget = new ExecutionTimeBudget(_clock, started, TimeSpan.FromSeconds(occurrence.Task.TimeoutSeconds), occurrence.Invocation.DeadlineUtc);
                        var manual = occurrence.Task.OwnerTaskId is not { } ownerId ? null :
                            await entry.ReadManualAsync(ownerId, readDeadline.Token).ConfigureAwait(false) ?? throw ScheduledProviderCodec.Invalid();
                        AcceptedProviderInvocation? accepted;
                        try { accepted = _publication.Accept(plans, occurrence, budget, minute.ObservedAtUtc, _stopping.Token, manual); }
                        catch (IOException error) when (error.Message is "scheduled_provider_minute_elapsed" or "schedule_admission_blocked")
                        { retryMinute |= error.Message == "schedule_admission_blocked"; continue; }
                        if (accepted is not null) lock (_gate) _held.Add(occurrence.Invocation.OccurrenceId, accepted);
                    }
                }
                if (!retryMinute) _scanned = scanningMinute;
            }
        }
        AcceptedProviderInvocation[] pending;
        lock (_gate) pending = _held.Values.Where(item => !_tasks.ContainsKey(item.Source.Document.OperationId)).ToArray();
        foreach (var work in pending)
        {
            lock (_gate) if (_tasks.Count >= MaximumConcurrent) break;
            var state = _publication.Read(work);
            if (state.Result is not null) { Finish(work, null); continue; }
            if (work.Budget.Read().IsExhausted)
            { Close(work, "scheduled_provider_deadline"); Finish(work, null); continue; }
            try
            {
                _publication.Begin(work);
                var task = Task.Run(() => ExecuteAsync(work));
                lock (_gate) _tasks.Add(work.Source.Document.OperationId, task);
            }
            catch (IOException error) when (error.Message == "scheduled_provider_clock_regressed")
            { Close(work, "scheduled_provider_clock_regressed"); Finish(work, null); }
            catch (InvalidOperationException error) when (error.Message is "scheduled_provider_revoked" or "scheduled_provider_unavailable")
            { Close(work, "scheduled_provider_revoked"); Finish(work, null); }
        }
    }

    public void AssertAllowed(FrozenScheduledInvocation invocation, string extensionId, string providerId) =>
        throw new UnauthorizedAccessException("scheduled_planned_context_required");

    public void AssertAllowed(ManualInvocation invocation, string extensionId, string providerId)
    {
        if (_manual is null) throw new UnauthorizedAccessException("manual_work_owner_unavailable");
        _manual.AssertAllowed(invocation, extensionId, providerId);
    }

    public void AssertAllowed(PlannedScheduledInvocation context, string extensionId, string providerId)
    {
        var work = RequireActual(context.Invocation, extensionId, providerId);
        if (context.PlansRevision != work.Source.Document.PlansRevision || context.TaskRevision != work.Source.Document.Task.Revision ||
            context.OwnerTaskId != work.Source.Document.Task.OwnerTaskId)
            throw new UnauthorizedAccessException("scheduled_planned_context_unbound");
        work.Entry.RequireExecution(context.OwnerTaskId ?? context.Invocation.SubjectId, context.Invocation.RunId);
        _lease.PublishExecution(() =>
        {
            work.Use(_publication, () => true);
            using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
            using var transaction = connection.BeginTransaction(deferred: true);
            var verified = new ScheduledProviderEvidence(_instance, _contracts).Read(work.Source.Reference, connection, transaction);
            if (verified.Source.Reference != work.Source.Reference || verified.Source.Document.Creator != JournalWriterStore.CurrentWriter ||
                verified.Source.Document.OwnerGeneration != _lease.StartupGeneration) throw ScheduledProviderCodec.Invalid();
            _publication.RequireOpen(work, connection, transaction, requireBegin: true);
        });
    }

    internal AcceptedProviderInvocation RequireActual(FrozenScheduledInvocation invocation, string extensionId,
        string? providerId = null, ExtensionScope? actualScope = null)
    {
        AcceptedProviderInvocation? work;
        lock (_gate) _held.TryGetValue(invocation.OccurrenceId, out work);
        if (work is null || !work.Source.Document.Invocation.Matches(invocation) || work.Entry.ExtensionId != extensionId ||
            providerId is not null && work.Entry.ProviderId != providerId || actualScope is not null &&
            (actualScope.ActivationId != work.Entry.ActivationId || !actualScope.IsPublished))
            throw new UnauthorizedAccessException("scheduled_invocation_not_active");
        work.Use(_publication, () => true); return work;
    }

    internal WorkNotificationOrigin BindNotification(ExtensionScope scope, NotificationSubmission submission)
    {
        _lease.VerifyHeld(_instance);
        if (submission.ExtensionId != scope.Activation.ExtensionId) throw new UnauthorizedAccessException("scheduled_invocation_not_active");
        var work = RequireActual(submission.Invocation, submission.ExtensionId, actualScope: scope);
        return new(work.Source.Reference, work.Source.Document.Invocation, work.Activity, work.Budget, _resources,
            AcceptedProviderInvocation.Claims(work.Entry.ExtensionId), "schedule_admission_blocked",
            (connection, transaction) => RequireSubmissionInside(work, scope, connection, transaction));
    }

    private void RequireSubmissionInside(AcceptedProviderInvocation work, ExtensionScope actualScope,
        Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        _ = RequireActual(work.Source.Document.Invocation, work.Entry.ExtensionId, work.Entry.ProviderId, actualScope);
        var verified = new ScheduledProviderEvidence(_instance, _contracts).Read(work.Source.Reference, connection, transaction);
        if (verified.Source.Reference != work.Source.Reference || verified.Source.Document.Creator != JournalWriterStore.CurrentWriter ||
                verified.Source.Document.OwnerGeneration != _lease.StartupGeneration) throw ScheduledProviderCodec.Invalid();
        _publication.RequireOpen(work, connection, transaction, requireBegin: true);
    }

    internal ResourceOperationLease AcquireCallbackRoot(AcceptedProviderInvocation work)
    {
        _ = RequireActual(work.Source.Document.Invocation, work.Entry.ExtensionId, work.Entry.ProviderId);
        return _resources.TryContinueRun(work.Activity, work.Source.Document.OperationId,
            AcceptedProviderInvocation.CallbackClaims(work.Entry.ExtensionId)).Lease
            ?? throw new IOException("schedule_admission_blocked");
    }

    internal void RequestStop()
    {
        _manual?.RequestStop();
        try { _stopping.Cancel(); }
        catch (AggregateException error) { lock (_gate) _cancellationFailure ??= error; }
    }

    internal Task StopAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_stopTask is not null) return _stopTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _stopTask = completion.Task;
        }
        _ = StopCoreAsync(completion); return completion.Task;
    }

    private async Task StopCoreAsync(TaskCompletionSource completion)
    {
        var failures = new List<Exception>();
        try
        {
            RequestStop();
            if (_manual is not null)
                try { await _manual.StopAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            AcceptedProviderInvocation[] all;
            lock (_gate) all = _held.Values.ToArray();
            foreach (var work in all)
                try { Close(work, "scheduled_provider_cancelled"); } catch (Exception error) { failures.Add(error); }
            Task<ScheduledBusinessResult?>[] tasks;
            lock (_gate) tasks = _tasks.Values.ToArray();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            try { await CollectReturnedAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            foreach (var work in all)
                try { if (_publication.Read(work).Result is null) Finish(work, null); } catch (Exception error) { failures.Add(error); }
            lock (_gate) if (_cancellationFailure is { } cancellation) failures.Add(cancellation);
            if (failures.Count != 0) completion.TrySetException(failures);
            else completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private async Task<ScheduledBusinessResult?> ExecuteAsync(AcceptedProviderInvocation work)
    {
        using var root = _resources.TryContinueRun(work.Activity, work.Source.Document.OperationId,
            AcceptedProviderInvocation.CallbackClaims(work.Entry.ExtensionId)).Lease ??
            throw new IOException("schedule_admission_blocked");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, work.Cancellation.Token);
        deadline.CancelAfter(work.Budget.Read().Remaining);
        try
        {
            return await work.Entry.DispatchPlannedAsync(new(work.Source.Document.Invocation, work.Source.Document.PlansRevision,
                work.Source.Document.Task.Revision, work.Source.Document.Task.OwnerTaskId), deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { Close(work, _stopping.IsCancellationRequested ? "scheduled_provider_cancelled" : "scheduled_provider_deadline"); return null; }
        catch (InvalidOperationException error) when (error.Message is "scheduled_provider_revoked" or "scheduled_provider_unavailable")
        { Close(work, "scheduled_provider_revoked"); return null; }
        catch { return null; }
    }

    private async Task CollectReturnedAsync()
    {
        KeyValuePair<Guid, Task<ScheduledBusinessResult?>>[] finished;
        lock (_gate) finished = _tasks.Where(pair => pair.Value.IsCompleted).ToArray();
        foreach (var pair in finished)
        {
            AcceptedProviderInvocation work; lock (_gate) work = _held[pair.Key];
            ScheduledBusinessResult? result = null;
            try { result = await pair.Value.ConfigureAwait(false); }
            catch { }
            Finish(work, result);
            lock (_gate) _tasks.Remove(pair.Key);
        }
    }

    private void Finish(AcceptedProviderInvocation work, ScheduledBusinessResult? result)
    {
        var state = _publication.Complete(work, result);
        if (state.Result is null) throw ScheduledProviderCodec.Invalid();
        lock (_gate) _held.Remove(work.Source.Document.OperationId);
        work.Activity.Dispose(); work.Cancellation.Dispose();
    }

    private void Close(AcceptedProviderInvocation work, string reason)
    {
        _publication.Close(work, reason);
        work.Cancellation.Cancel();
    }
}
