using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Extensions;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal sealed record ManualRunRequest(Guid OperationId, string CallerSubject, Guid ClientId, string ProviderId, Guid TaskId, long TaskRevision);
internal sealed record ManualRunAcceptance(ManualWorkState State, bool IsNew);

[SupportedOSPlatform("windows")]
internal sealed class ManualWorkRuntime(ManualWorkStore store, ScheduledWorkRegistry registry, TimeProvider clock,
    Core.Configuration.RecoveryIndex recovery) : IManualInvocationAuthorization
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, AcceptedManualWork> _held = [];
    private readonly Dictionary<Guid, Task> _tasks = [];
    private readonly SemaphoreSlim _admissions = new(1, 1);
    private readonly SemaphoreSlim _slots = new(4, 4);
    private readonly CancellationTokenSource _stopping = new();
    private Task? _stopTask;
    private string? _failure;
    internal string? FailureCode { get { lock (_gate) return _failure; } }
    internal bool UsesRegistry(ScheduledWorkRegistry expected) => ReferenceEquals(registry, expected);

    internal async ValueTask<ManualRunAcceptance> RunAsync(ExtensionScope scope, ManualRunRequest request,
        Action requireCaller, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellation, scope.Cancellation, _stopping.Token);
        reading.CancelAfter(TimeSpan.FromSeconds(30));
        await _admissions.WaitAsync(reading.Token).ConfigureAwait(false);
        try
        {
            requireCaller(); RequireRunning();
            if (store.Lookup(request.OperationId, request.CallerSubject, scope.Activation.ExtensionId, request.ProviderId,
                request.TaskId, request.TaskRevision) is { } replay) return new(replay, false);
            lock (_gate) if (_held.Count >= 128) throw new IOException("manual_work_capacity");
            var started = clock.GetUtcNow(); var timestamp = clock.GetTimestamp();
            var entry = registry.RequireManualEntry(scope.Activation.ExtensionId, request.ProviderId);
            if (entry.ActivationId != scope.ActivationId || !scope.IsPublished) throw new UnauthorizedAccessException("manual_work_activation_unbound");
            var observed = await entry.ReadManualAsync(request.TaskId, reading.Token).ConfigureAwait(false) ?? throw new IOException("manual_work_task_missing");
            reading.Token.ThrowIfCancellationRequested(); requireCaller(); RequireRunning();
            if (observed.Task.Revision != request.TaskRevision) throw new IOException("manual_work_revision_conflict");
            var budget = new ExecutionTimeBudget(clock, timestamp, TimeSpan.FromSeconds(observed.Task.TimeoutSeconds),
                started.AddSeconds(observed.Task.TimeoutSeconds));
            var work = store.Accept(observed, request.OperationId, request.CallerSubject, request.ClientId, started, budget,
                () => { reading.Token.ThrowIfCancellationRequested(); requireCaller(); RequireRunning(); });
            var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task task;
            try
            {
                using (ExecutionContext.SuppressFlow()) task = Task.Run(async () =>
                { await launch.Task.ConfigureAwait(false); await ExecuteAsync(work).ConfigureAwait(false); });
                lock (_gate) { _held.Add(request.OperationId, work); _tasks.Add(request.OperationId, task); }
                var accepted = store.Read(work);
                launch.TrySetResult(); return new(accepted, true);
            }
            catch
            {
                launch.TrySetCanceled();
                try
                {
                    _ = store.Complete(work, null, "manual_work_launch_failed"); work.Dispose();
                    lock (_gate) { _held.Remove(request.OperationId); _tasks.Remove(request.OperationId); }
                }
                catch { Fail("manual_work_completion_unverified"); }
                throw;
            }
        }
        finally { _admissions.Release(); }
    }

    public void AssertAllowed(FrozenScheduledInvocation invocation, string extensionId, string providerId) =>
        throw new UnauthorizedAccessException("manual_work_context_required");

    public void AssertAllowed(ManualInvocation invocation, string extensionId, string providerId) =>
        _ = RequireActual(invocation, extensionId, providerId);

    internal async ValueTask<IDisposable> QuiesceAsync(ExtensionScope scope, string providerId, Guid taskId, long revision,
        CancellationToken cancellation)
    {
        RequireRunning();
        if (revision is < 0 or > 9007199254740991) throw new ArgumentException("manual_work_revision_invalid");
        var entry = registry.RequireManualEntry(scope.Activation.ExtensionId, providerId);
        if (entry.ActivationId != scope.ActivationId || !scope.IsPublished) throw new UnauthorizedAccessException("manual_work_activation_unbound");
        var before = await entry.ReadManualAsync(taskId, cancellation).ConfigureAwait(false);
        if (before?.Task.Revision != (revision == 0 ? null : revision)) throw new IOException("manual_work_revision_conflict");
        var ids = (before?.Task.ScheduledTaskIds.ToArray() ?? []).Append(taskId).Distinct().ToArray();
        var draining = entry.QuiesceAsync(ids, cancellation).AsTask();
        List<Exception>? failures = null;
        AcceptedManualWork[] manual;
        lock (_gate) manual = _held.Values.Where(work => work.Entry.ActivationId == entry.ActivationId &&
            work.Entry.ProviderId == providerId && work.Source.Document.Task.TaskId == taskId).ToArray();
        foreach (var work in manual)
            try { work.Cancellation.Cancel(); } catch (AggregateException error) { (failures ??= []).Add(error); }
        var hold = await draining.ConfigureAwait(false);
        try
        {
            if (failures is not null) throw new AggregateException(failures);
            var after = await entry.ReadManualAsync(taskId, cancellation).ConfigureAwait(false);
            if (after?.Task.Revision != before?.Task.Revision || after is not null &&
                !ScheduledProviderCodec.Encode(after.Task).AsSpan().SequenceEqual(ScheduledProviderCodec.Encode(before!.Task)))
                throw new IOException("manual_work_revision_conflict");
            RequireRunning(); cancellation.ThrowIfCancellationRequested(); return hold;
        }
        catch { hold.Dispose(); throw; }
    }

    internal AcceptedManualWork RequireActual(ManualInvocation context, string extensionId, string? providerId = null, ExtensionScope? scope = null)
    {
        AcceptedManualWork? work;
        lock (_gate) _held.TryGetValue(context.Invocation.OccurrenceId, out work);
        if (work is null || !work.Source.Document.Invocation.Matches(context.Invocation) || context.TaskRevision != work.Source.Document.Task.Revision ||
            work.Entry.ExtensionId != extensionId || providerId is not null && work.Entry.ProviderId != providerId ||
            scope is not null && (scope.ActivationId != work.Entry.ActivationId || !scope.IsPublished))
            throw new UnauthorizedAccessException("manual_work_invocation_inactive");
        work.Entry.Use(() => true); work.Cancellation.Token.ThrowIfCancellationRequested();
        work.Entry.RequireExecution(work.Source.Document.Task.TaskId, context.Invocation.RunId);
        if (work.Budget.Read().IsExhausted) throw new IOException("manual_work_deadline");
        var state = store.Read(work);
        if (state.Begin is null || state.Result is not null) throw new UnauthorizedAccessException("manual_work_invocation_inactive");
        return work;
    }

    internal void RequestStop()
    {
        try { _stopping.Cancel(); }
        catch (AggregateException) { Fail("manual_work_cancellation_failed"); }
    }

    internal Task StopAsync()
    {
        TaskCompletionSource stopped;
        lock (_gate)
        {
            if (_stopTask is not null) return _stopTask;
            stopped = new(TaskCreationOptions.RunContinuationsAsynchronously); _stopTask = stopped.Task;
        }
        RequestStop(); _ = DrainAsync(stopped); return stopped.Task;
    }

    private async Task DrainAsync(TaskCompletionSource stopped)
    {
        try
        {
            await _admissions.WaitAsync().ConfigureAwait(false);
            Task[] tasks;
            try { lock (_gate) tasks = _tasks.Values.ToArray(); }
            finally { _admissions.Release(); }
            await Task.WhenAll(tasks).ConfigureAwait(false);
            lock (_gate) if (_held.Count != 0 || _failure is not null) throw new IOException(_failure ?? "manual_work_drain_unverified");
            stopped.TrySetResult();
        }
        catch (Exception error) { stopped.TrySetException(error); }
    }

    private async Task ExecuteAsync(AcceptedManualWork work)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, work.Cancellation.Token);
        deadline.CancelAfter(work.Budget.Read().Remaining);
        WorkTaskUse? taskUse = null; var slot = false;
        ScheduledBusinessResult? returned = null; var reason = "manual_work_unverified";
        try
        {
            await _slots.WaitAsync(deadline.Token).ConfigureAwait(false); slot = true;
            taskUse = work.Entry.EnterTask(work.Source.Document.Task.TaskId);
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, taskUse.Cancellation);
            var observed = await work.Entry.ReadManualAsync(work.Source.Document.Task.TaskId, invocation.Token).ConfigureAwait(false);
            if (observed is null || !ScheduledProviderCodec.Encode(observed.Task).AsSpan().SequenceEqual(ScheduledProviderCodec.Encode(work.Source.Document.Task)))
                throw new IOException("manual_work_revision_conflict");
            invocation.Token.ThrowIfCancellationRequested();
            store.Begin(work);
            returned = await work.Entry.DispatchManualAsync(new(work.Source.Document.Invocation, work.Source.Document.Task.Revision), invocation.Token).ConfigureAwait(false);
            invocation.Token.ThrowIfCancellationRequested();
            reason = "manual_work_returned";
        }
        catch (OperationCanceledException) { reason = _stopping.IsCancellationRequested ? "manual_work_cancelled" : "manual_work_interrupted"; }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        { reason = "manual_work_unverified"; }
        catch { reason = "manual_work_callback_failed"; }
        finally
        {
            try
            {
                if (deadline.IsCancellationRequested || taskUse?.Cancellation.IsCancellationRequested == true) returned = null;
                _ = store.Complete(work, returned, reason);
                lock (_gate) { _held.Remove(work.Source.Document.OperationId); _tasks.Remove(work.Source.Document.OperationId); }
                work.Dispose();
            }
            catch { Fail("manual_work_completion_unverified"); }
            finally { taskUse?.Dispose(); if (slot) _slots.Release(); }
        }
    }

    private void RequireRunning()
    {
        _stopping.Token.ThrowIfCancellationRequested();
        lock (_gate) if (_failure is not null) throw new IOException(_failure);
    }

    private void Fail(string code)
    {
        lock (_gate) _failure ??= code;
        recovery.BlockUnknown(code);
    }
}
