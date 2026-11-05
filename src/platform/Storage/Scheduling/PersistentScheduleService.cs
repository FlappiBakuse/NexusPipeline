using NexusPipeline.Platform.Storage.Automation;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Storage.Scheduling;

[SupportedOSPlatform("windows")]
public sealed class PersistentScheduleService
{
    private readonly object _gate = new();
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly HostLifecycle _lifecycle;
    private readonly RecoveryIndex _recovery;
    private readonly ExecutionAdmissionService _execution;
    private readonly TimeProvider _clock;
    private readonly StartupQueueStore _startup;
    private readonly ContractReader _contracts;
    private readonly ScheduledProviderDispatch _providers;
    private readonly ManualWorkRuntime _manual;
    private readonly WorkRuntimeFacts _workFacts;
    private IWorkQueueAcceptance? _notificationQueue;
    private ScheduleRuntimeService? _runtime;
    private ScheduleOccurrenceStore? _store;
    private Task _completion = Task.CompletedTask;
    private bool _stopping;
    private bool _wasStopped;
    public string? FailureCode { get; private set; }
    public bool IsRunning { get { lock (_gate) return _runtime is not null && !_completion.IsCompleted && !_stopping && FailureCode is null; } }
    public IReadOnlyList<ScheduleDiagnostic> Diagnostics { get { lock (_gate) return _store?.Diagnostics ?? []; } }
    public Task Completion { get { lock (_gate) return _completion; } }

    public PersistentScheduleService(RuntimeInstance instance, StateDatabase database, InstanceLease lease, ResourceLeaseManager resources,
        HostLifecycle lifecycle, RecoveryIndex recovery, ExecutionAdmissionService execution, TimeProvider? clock = null, ContractReader? contracts = null)
    {
        lease.VerifyHeld(instance);
        if (!resources.UsesRecoveryIndex(recovery) || !lifecycle.UsesRecoveryIndex(recovery) ||
            database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot) throw StartupQueueRows.Invalid();
        StartupQueueInventory.Verify(instance, database);
        _instance = instance; _database = database; _lease = lease; _resources = resources; _lifecycle = lifecycle;
        _recovery = recovery; _execution = execution; _clock = clock ?? TimeProvider.System;
        _contracts = contracts ?? new();
        ScheduledProviderInventory.Visit(instance, database, _contracts, _ => { });
        WorkEffectInventory.Verify(instance, database, _contracts);
        var providerPublication = new ScheduledProviderPublication(instance, database, _contracts, lease, resources, _clock);
        if (!providerPublication.ReconcilePriorOwners())
        {
            FailureCode = "schedule_provider_recovery_unverified";
            _recovery.BlockUnknown(FailureCode);
        }
        _providers = new(instance, database, _contracts, lease, resources, _clock);
        var manualStore = new ManualWorkStore(instance, database, lease, resources, _contracts, _clock);
        if (!manualStore.ReconcilePriorOwners())
        {
            FailureCode = "manual_work_recovery_unverified";
            _recovery.BlockUnknown(FailureCode);
        }
        _manual = new(manualStore, _providers.Registry, _clock, recovery);
        _providers.BindManual(_manual);
        _workFacts = new(instance, database, lease, _providers.Registry, _contracts, _clock);
        _startup = new(instance, database, lease, resources, _clock);
        _startup.RetirePriorOwners();
    }

    internal void RequireOwner(RuntimeInstance instance, StateDatabase database, InstanceLease lease, ResourceLeaseManager resources)
    {
        _lease.VerifyHeld(_instance);
        if (_instance.InstanceId != instance.InstanceId || _database.DatabasePath != database.DatabasePath ||
            !ReferenceEquals(_lease, lease) || !ReferenceEquals(_resources, resources))
            throw new IOException("schedule_service_instance_unbound");
    }

    internal bool ObserveEffectOwner(string extensionId, string providerId, Guid taskId, Guid operationId, Guid runId,
        Guid attemptId, IInstalledWork actual, CancellationToken cancellation) =>
        _workFacts.ObserveOwner(extensionId, providerId, taskId, operationId, runId, attemptId, actual, cancellation);

    internal bool ObserveEffectOperation(string extensionId, Guid operationId, Guid runId, Guid attemptId, IInstalledWork actual, CancellationToken cancellation) =>
        _workFacts.ObserveOperation(extensionId, operationId, runId, attemptId, actual, cancellation);

    public ScheduleRegistrationPort CreateSchedulePort(ExtensionManifest manifest, ExtensionScope actualScope)
    {
        _lease.VerifyHeld(_instance);
        return new(manifest, actualScope, _providers.Registry, _contracts);
    }

    public NotificationSubmissionPort CreateNotificationPort(ExtensionManifest manifest, ExtensionScope actualScope)
    {
        _lease.VerifyHeld(_instance);
        var backend = new ScheduledNotificationSubmission(_instance, _database, _contracts, _lease, _providers, _resources,
            actualScope, _notificationQueue ?? throw new IOException("notification_runtime_unavailable"));
        return new(manifest, actualScope, backend, backend, _contracts);
    }

    internal NotificationSubmissionPort CreateManualNotifications(ExtensionManifest manifest, ExtensionScope scope, PreparedManualCallback work)
    {
        _lease.VerifyHeld(_instance);
        var backend = new ManualNotificationSubmission(_instance, _database, _contracts, _lease, _resources, scope, work,
            _notificationQueue ?? throw new IOException("notification_runtime_unavailable"));
        return new(manifest, scope, backend, backend, _contracts);
    }

    internal PreparedScheduledCallback PrepareInstalledCallback(ExtensionScope scope, ScheduledPlannedInvocation invocation)
    {
        _lease.VerifyHeld(_instance);
        var wire = _contracts.Read<ScheduledPlannedInvocation>(BoundedJson.Serialize(invocation),
            "scheduled-planned-invocation.schema.json");
        var context = new NexusPipeline.Core.Scheduling.PlannedScheduledInvocation(
            ScheduledInvocationWire.FromWire(wire.Invocation), wire.PlansRevision, wire.TaskRevision,
            wire.OwnerTaskId is null ? null : Guid.ParseExact(wire.OwnerTaskId, "D"));
        var work = _providers.RequireActual(context.Invocation, scope.Activation.ExtensionId, actualScope: scope);
        _providers.AssertAllowed(context, scope.Activation.ExtensionId, work.Entry.ProviderId);
        return new(_providers, scope, work, context, wire);
    }

    internal PreparedManualCallback PrepareManualCallback(ExtensionScope scope, ManualWorkInvocation invocation)
    {
        _lease.VerifyHeld(_instance);
        var wire = _contracts.Read<ManualWorkInvocation>(BoundedJson.Serialize(invocation), "manual-work-invocation.schema.json");
        var context = new ManualInvocation(ScheduledInvocationWire.FromWire(wire.Invocation), wire.TaskRevision);
        var work = _manual.RequireActual(context, scope.Activation.ExtensionId, scope: scope);
        return new(_manual, _resources, scope, work, context, wire);
    }

    internal ValueTask<ManualRunAcceptance> RunManualAsync(ExtensionScope scope, ManualRunRequest request, Action requireCaller,
        CancellationToken cancellation)
    {
        _lease.VerifyHeld(_instance);
        if (FailureCode is not null) throw new IOException(FailureCode);
        return _manual.RunAsync(scope, request, requireCaller, cancellation);
    }

    internal (DateTimeOffset UtcNow, string TimeZoneId) ReadClock()
    {
        _lease.VerifyHeld(_instance);
        return (_clock.GetUtcNow(), _clock.LocalTimeZone.Id);
    }

    internal ValueTask<NexusPipeline.Extensibility.Scheduling.WorkTaskSnapshot> ReadWorkAsync(ExtensionScope scope, string providerId,
        IReadOnlyList<Guid> taskIds, CancellationToken cancellation) => _workFacts.ReadAsync(scope, providerId, taskIds, cancellation);

    internal bool HasStopped(ExtensionScope scope, string providerId, Guid taskId, Guid runId, Guid attemptId,
        Storage.Extensions.IInstalledWork actual, CancellationToken cancellation) =>
        _workFacts.HasStopped(scope, providerId, taskId, runId, attemptId, actual, cancellation);

    internal ValueTask<IDisposable> QuiesceAsync(ExtensionScope scope, string providerId, Guid taskId, long revision, CancellationToken cancellation)
    {
        _lease.VerifyHeld(_instance);
        if (FailureCode is not null) throw new IOException(FailureCode);
        return _manual.QuiesceAsync(scope, providerId, taskId, revision, cancellation);
    }

    internal void BindNotificationQueue(IWorkQueueAcceptance queue)
    {
        ArgumentNullException.ThrowIfNull(queue); _lease.VerifyHeld(_instance);
        lock (_gate)
        {
            if (_notificationQueue is not null && !ReferenceEquals(_notificationQueue, queue))
                throw new InvalidOperationException("notification_queue_owner_changed");
            _notificationQueue = queue;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            _lease.VerifyHeld(_instance);
            if (_wasStopped) throw new InvalidOperationException("schedule_service_stopped");
            if (_runtime is not null || _stopping) throw new InvalidOperationException("schedule_already_started");
            if (FailureCode is not null) throw new IOException(FailureCode);
            var store = new ScheduleOccurrenceStore(_instance, _database, _lease, _resources, _clock);
            _store = store;
            var admission = new ScheduledExecutionAdmission(_instance, _database, _lease, _execution, _clock);
            var startup = new StartupQueueDispatch(_instance, _database, _lease, _startup, _execution, _clock);
            var runtime = new ScheduleRuntimeService(store, admission, _clock, startup, _lifecycle, _providers,
                new RunDaysMaintenance(_instance, _database, _lease, _resources, _clock));
            _runtime = runtime;
            // Publish initial metadata before readiness; otherwise first writes race the startup resource leases.
            _completion = Observe(runtime);
        }
    }

    public bool BlocksAutomaticUpdate(TimeSpan horizon)
    {
        _lease.VerifyHeld(_instance);
        var recovery = _recovery.Current;
        if (FailureCode is not null || !recovery.IsReady || recovery.GlobalBlockReason is not null || _startup.HasPending()) return true;
        if (new ScheduleOccurrenceStore(_instance, _database, _lease, _resources, _clock).ReadWaiting(1).Length != 0) return true;
        return NexusPipeline.Core.Scheduling.ScheduleMinutePolicy.Upcoming(new AutomationDefinitionStore(_database).Read(),
            _clock.GetUtcNow(), horizon) is not null;
    }

    public async Task StopAsync()
    {
        ScheduleRuntimeService? runtime; Task completion;
        lock (_gate) { _stopping = true; runtime = _runtime; completion = _completion; }
        try
        {
            if (runtime is not null)
            {
                try { await runtime.StopAsync().ConfigureAwait(false); }
                catch (Exception error) when (IsMetadataFailure(error))
                { await completion.ConfigureAwait(false); if (FailureCode is null) throw; }
            }
            await completion.ConfigureAwait(false);
            await _providers.StopAsync().ConfigureAwait(false);
        }
        finally { lock (_gate) { if (ReferenceEquals(_runtime, runtime)) _runtime = null; _stopping = false; _wasStopped = true; } }
    }

    private async Task Observe(ScheduleRuntimeService runtime)
    {
        try { runtime.Initialize(); await runtime.RunAsync().ConfigureAwait(false); }
        catch (Exception)
        {
            FailureCode = "schedule_reconciliation_required";
            _recovery.BlockUnknown(FailureCode);
        }
    }

    private static bool IsMetadataFailure(Exception error) => error is IOException or SqliteException or JsonException or InvalidOperationException or
        ArgumentException or FormatException or OverflowException or KeyNotFoundException;
}
