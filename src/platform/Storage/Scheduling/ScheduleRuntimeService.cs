using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Scheduling;
using NexusPipeline.Platform.Storage.Automation;

namespace NexusPipeline.Platform.Storage.Scheduling;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class ScheduleRuntimeService(IScheduleOccurrenceStore store, IScheduledExecutionAdmission admission, TimeProvider clock,
    StartupQueueDispatch? startup = null, NexusPipeline.Core.Maintenance.HostLifecycle? lifecycle = null,
    ScheduledProviderDispatch? providers = null, RunDaysMaintenance? runDays = null)
{
    private readonly ScheduleTick _ticks = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task _loop = Task.CompletedTask;
    private int _started;
    private long? _highestCommittedMinute;

    internal void Initialize()
    {
        runDays?.Tick();
        startup?.Initialize();
        CommitMinute();
    }

    internal Task RunAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("schedule_already_started");
        return _loop = RunCoreAsync();
    }

    internal Task TickAsync() => _ticks.Run(async () =>
    {
        if (_stopping.IsCancellationRequested || lifecycle?.Phase == NexusPipeline.Core.Maintenance.HostLifecyclePhase.ClosePermitted) return;
        var canCommit = lifecycle is null || lifecycle.Phase == NexusPipeline.Core.Maintenance.HostLifecyclePhase.Running;
        if (canCommit) runDays?.Tick();
        startup?.Tick(_stopping.Token, canCommit);
        if (canCommit) CommitMinute();
        if (providers is not null) await providers.TickAsync(canCommit).ConfigureAwait(false);
        foreach (var occurrence in store.ReadWaiting(64))
        {
            if (_stopping.IsCancellationRequested) break;
            try { _ = await admission.StartAsync(occurrence, _stopping.Token).ConfigureAwait(false); }
            catch (ExecutionSelectionException error) when (error.Message is "execution_resource_conflict" or "execution_host_unavailable" or "execution_admission_inbox_full" or
                "execution_catalog_revision_conflict" or "execution_settings_revision_conflict")
            { }
            catch (ExecutionSelectionException error) when (error.Message == "execution_schedule_cancelled")
            { _ = store.BlockWaiting(occurrence, "schedule_source_cancelled"); }
            catch (ExecutionSelectionException) { _ = store.BlockWaiting(occurrence, "schedule_source_unavailable"); }
            catch (NexusPipeline.Core.Configuration.ConfigurationException)
            { _ = store.BlockWaiting(occurrence, "schedule_source_unavailable"); }
            catch (IOException error) when (error.Message == "execution_budget_continuity_unknown")
            { _ = store.BlockWaiting(occurrence, "schedule_budget_continuity_unknown"); }
        }
    });

    private void CommitMinute()
    {
        var now = clock.GetUtcNow();
        if (now.Offset != TimeSpan.Zero || now < DateTimeOffset.UnixEpoch) throw new IOException("schedule_clock_invalid");
        var minute = now.ToUnixTimeSeconds() / 60;
        if (_highestCommittedMinute is not null && minute <= _highestCommittedMinute) return;
        _ = store.CommitCurrentMinute();
        _highestCommittedMinute = Math.Max(minute, store.ReadCheckpoint().Watermark.HighestUtcMinute ?? minute);
    }

    internal async Task StopAsync()
    {
        _stopping.Cancel(); providers?.RequestStop();
        await _ticks.StopAsync().ConfigureAwait(false);
        if (providers is not null) await providers.StopAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    private async Task RunCoreAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                try { await TickAsync().ConfigureAwait(false); }
                catch (IOException error) when (error.Message == "schedule_admission_blocked") { }
                catch (InvalidOperationException error) when (_stopping.IsCancellationRequested && error.Message == "schedule_stopping") { break; }
                if (_stopping.IsCancellationRequested || !await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false)) break;
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }
}
