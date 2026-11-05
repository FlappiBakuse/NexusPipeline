using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Scheduling;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Scheduling;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledWorkTasks(InstalledExtensionConsumption consumption, PersistentScheduleService scheduling,
    ExtensionScope scope, bool allowed) : IWorkTasks
{
    public async ValueTask<WorkTaskSnapshot> ReadAsync(string providerId, IReadOnlyList<Guid> taskIds, CancellationToken cancellationToken)
    {
        Require(); using var use = await consumption.BeginAsync(cancellationToken).ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, consumption.CallbackLifetime);
        var result = await scheduling.ReadWorkAsync(scope, providerId, taskIds, lifetime.Token).ConfigureAwait(false);
        use.RequireCurrent(); return result;
    }

    public ValueTask<bool> HasStoppedAsync(string providerId, Guid taskId, Guid runId, Guid attemptId, CancellationToken cancellationToken)
    {
        Require(); cancellationToken.ThrowIfCancellationRequested(); using var use = consumption.Begin();
        var actual = consumption.Work ?? throw new UnauthorizedAccessException("work_callback_required");
        var result = scheduling.HasStopped(scope, providerId, taskId, runId, attemptId, actual, cancellationToken);
        use.RequireCurrent(); return ValueTask.FromResult(result);
    }

    public ValueTask<WorkClock> ReadClockAsync(CancellationToken cancellationToken)
    {
        Require(); cancellationToken.ThrowIfCancellationRequested();
        using var use = consumption.Begin();
        var current = scheduling.ReadClock();
        use.RequireCurrent(); return ValueTask.FromResult(new WorkClock(current.UtcNow, current.TimeZoneId));
    }

    public ValueTask<WorkInvocation> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        Require(); cancellationToken.ThrowIfCancellationRequested();
        using var use = consumption.Begin();
        var actual = consumption.Work ?? throw new UnauthorizedAccessException("work_callback_required");
        actual.RequireCurrent();
        var invocation = actual.FrozenInvocation;
        var result = new WorkInvocation(invocation.RunId, invocation.AttemptId, invocation.SubjectId, actual.TaskId,
            actual.TaskRevision, actual.LocalDate, actual.TimeZoneId, invocation.DeadlineUtc);
        use.RequireCurrent(); return ValueTask.FromResult(result);
    }

    public async ValueTask<ManualRunReceipt> RunAsync(string providerId, Guid taskId, long expectedRevision, CancellationToken cancellationToken)
    {
        Require();
        using var use = await consumption.BeginAsync(cancellationToken).ConfigureAwait(false);
        if (consumption.FrontendOperationId is not { } operation || consumption.ClientId is not { } clientId)
            throw new UnauthorizedAccessException("manual_work_frontend_required");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, consumption.CallbackLifetime);
        var receipt = await scheduling.RunManualAsync(scope, new(operation, consumption.SubjectId, clientId, providerId, taskId, expectedRevision),
            use.RequireCurrent, lifetime.Token).ConfigureAwait(false);
        use.RequireCurrent(); var invocation = receipt.State.Source.Document.Invocation;
        return new(operation, invocation.RunId, invocation.AttemptId, receipt.IsNew);
    }

    public async ValueTask<IAsyncDisposable> QuiesceAsync(string providerId, Guid taskId, long expectedRevision, CancellationToken cancellationToken)
    {
        Require();
        var use = await consumption.BeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (consumption.FrontendOperationId is null || consumption.ClientId is null)
                throw new UnauthorizedAccessException("manual_work_frontend_required");
            var actualLifetime = consumption.CallbackLifetime;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, actualLifetime);
            var held = await scheduling.QuiesceAsync(scope, providerId, taskId, expectedRevision, lifetime.Token).ConfigureAwait(false);
            try { use.RequireCurrent(); return new Hold(held, use, cancellationToken, actualLifetime); }
            catch { held.Dispose(); throw; }
        }
        catch { use.Dispose(); throw; }
    }

    private void Require()
    {
        if (!allowed) throw new UnauthorizedAccessException("schedule_register_capability_required");
    }

    private sealed class Hold : IAsyncDisposable
    {
        private readonly IDisposable _held;
        private readonly InstalledExtensionConsumption.Use _use;
        private readonly CancellationTokenSource _lifetime;
        private CancellationTokenRegistration _retire;
        private int _disposed;
        internal Hold(IDisposable held, InstalledExtensionConsumption.Use use, CancellationToken requested, CancellationToken actual)
        {
            _held = held; _use = use; _lifetime = CancellationTokenSource.CreateLinkedTokenSource(requested, actual);
            _retire = _lifetime.Token.Register(Dispose);
            if (Volatile.Read(ref _disposed) != 0) _retire.Dispose();
        }
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
        private void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _held.Dispose(); }
            finally { try { _use.Dispose(); } finally { _retire.Dispose(); _lifetime.Dispose(); } }
        }
    }
}
