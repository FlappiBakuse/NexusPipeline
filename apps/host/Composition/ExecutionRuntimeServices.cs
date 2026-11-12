using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Host.Lifecycle;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Host.Composition;

internal sealed class ExecutionRuntimeServices : IAsyncDisposable
{
    private readonly CancellationTokenSource _retryStopping = new();
    private CancellationTokenRegistration _shutdown;
    private Task _consumerTask = Task.CompletedTask;
    private Task _retryTask = Task.CompletedTask;
    private Task _retentionTask = Task.CompletedTask;
    private readonly NexusPipeline.Platform.Storage.Records.RecordHistoryRetention _retention;
    private int _stopped;
    internal HistoryProjection History { get; }
    internal ExecutionAdmissionService Admission { get; }
    internal ExecutionRuntimeConsumer Consumer { get; }
    internal ExecutionLiveMedia LiveMedia { get; }
    internal PersistentScheduleService Scheduling { get; }
    internal ExecutionRetryService Retry { get; }
    internal CompletionActionService Completion { get; }
    internal LocalCompletionAuthorization Authorization { get; }
    internal NotificationRuntime Notifications { get; }
    internal HostCompletionExit Exit { get; } = new();

    internal ExecutionRuntimeServices(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        ResourceLeaseManager resources, HostLifecycle lifecycle, RecoveryIndex recovery, ExecutionProviderRegistry providers, SecretFieldService secrets,
        NativeClientBinding? nativeBinding = null, NotificationStartupState? notifications = null)
    {
        using var paths = DirectoryAttributeScope.Hold(instance);
        using var blobs = BlobReadScope.Hold(instance);
        var inbox = new ExecutionAdmissionInbox();
        _retention = new(database, new(instance, database, contracts, lease, resources, recovery));
        Admission = new(instance, database, contracts, lease, resources, recovery, inbox, new(5, 5, 30),
            judgementRuntime: new(PhysicalPathPolicy.RuntimeProcessPath(),
                JudgementRuntimeDiscovery.FindPython()), providers: providers);
        History = new(instance, database, contracts, lease);
        Notifications = new(instance, database, contracts, lease, resources, recovery, secrets);
        try { Notifications.InitializeStartup(notifications); }
        catch { Notifications.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
        var finality = new ExecutionRunFinality(instance, database, contracts);
        try { Authorization = new(instance, database, contracts, lease, finality, nativeBinding); }
        catch { Notifications.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
        try { Completion = new(instance, database, contracts, lease, lifecycle, resources, recovery, finality, Authorization, Exit); }
        catch
        {
            Authorization.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Notifications.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw;
        }
        var terminal = new TerminalObserver(Notifications, Completion);
        LiveMedia = new(instance.InstanceId);
        Consumer = new(instance, database, contracts, lease, resources, recovery, inbox, terminal, LiveMedia, new(instance, database, contracts, lease, recovery));
        Retry = new(instance, database, contracts, lease, Admission, terminal);
        try
        {
            Scheduling = new(instance, database, lease, resources, lifecycle, recovery, Admission, contracts: contracts);
            Notifications.BindSchedules(Scheduling);
        }
        catch
        {
            try { Completion.StopAsync().GetAwaiter().GetResult(); }
            finally
            {
                try { Notifications.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                finally { Authorization.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            throw;
        }
    }

    internal void Start(IHostApplicationLifetime lifetime)
    {
        Exit.Bind(lifetime);
        _shutdown = lifetime.ApplicationStopping.Register(_retryStopping.Cancel);
        Notifications.Start();
        _ = Completion.RunAsync();
        _consumerTask = Consumer.RunAsync(lifetime.ApplicationStopping);
        Scheduling.Start();
        _retryTask = Retry.RunAsync(_retryStopping.Token);
        _retentionTask = Task.Run(async () =>
        {
            await History.RunAsync(_retryStopping.Token).ConfigureAwait(false);
            if (History.IsComplete) await _retention.RunAsync(_retryStopping.Token).ConfigureAwait(false);
        });
    }

    internal async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        try { await Scheduling.StopAsync().ConfigureAwait(false); }
        finally
        {
            _shutdown.Dispose(); _retryStopping.Cancel();
            try { await Task.WhenAll(_retryTask, _retentionTask).ConfigureAwait(false); }
            finally
            {
                try { await _consumerTask.ConfigureAwait(false); }
                finally
                {
                    try { await Completion.StopAsync().ConfigureAwait(false); }
                    finally
                    {
                        try { await Notifications.DisposeAsync().ConfigureAwait(false); }
                        finally { await Authorization.DisposeAsync().ConfigureAwait(false); _retryStopping.Dispose(); }
                    }
                }
            }
        }
    }
    public ValueTask DisposeAsync() => new(StopAsync());

    private sealed class TerminalObserver(NotificationRuntime notifications, CompletionActionService completion)
        : IAttemptTerminalObserver
    {
        public void OnCommitted(AttemptReceipt receipt)
        { notifications.OnCommitted(receipt); completion.OnCommitted(receipt); }
    }
}
