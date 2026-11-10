using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Host.Lifecycle;

[SupportedOSPlatform("windows")]
internal sealed class HostMaintenanceController : IAsyncDisposable
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ContractReader _contracts;
    private readonly PayloadApplyRoots _roots;
    private readonly HostLifecycle _lifecycle;
    private readonly ResourceLeaseManager _resources;
    private readonly IHostApplicationLifetime _application;
    private readonly NativeClientBinding? _desktop;
    private readonly NexusPipeline.Host.Desktop.OwnedDesktop? _ownedDesktop;
    private readonly MaintenanceStartupOrigin? _inheritedOrigin;
    private readonly NexusPipeline.Platform.Storage.Scheduling.PersistentScheduleService? _scheduling;
    private readonly UpdateWindowIntent? _window;
    private readonly ILogger? _logger;
    private readonly UpdateReleaseTrust? _trust = UpdateReleaseTrust.Configured();
    private PreparedUpdateHandoff? _handoff;
    private MaintenanceBootstrapPayload? _bootstrap;
    private HelperReadyServer? _ready;
    private MaintenanceStartupJob? _helper;
    private MaintenanceStartupOrigin? _ownedOrigin;
    private HostCompletionDrain? _drain;
    private Task? _flight;
    private Guid _operationId;
    private string? _subject;
    private bool _handedOff, _stopping;
    private HostRestartHandoff? _restart;
    private ResourceOperationLease? _restartRoot;
    private NativeClientBinding? _updateWindow;
    internal string? FailureCode { get; private set; }
    internal bool HasDesktop => _ownedDesktop is not null;

    internal HostMaintenanceController(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ContractReader contracts, PayloadApplyRoots roots, HostLifecycle lifecycle, ResourceLeaseManager resources,
        IHostApplicationLifetime application, NativeClientBinding? desktop, MaintenanceStartupOrigin? inheritedOrigin,
        NexusPipeline.Host.Desktop.OwnedDesktop? ownedDesktop = null,
        NexusPipeline.Platform.Storage.Scheduling.PersistentScheduleService? scheduling = null,
        UpdateWindowIntent? window = null, ILogger? logger = null)
    {
        _instance = instance; _database = database; _lease = lease; _contracts = contracts; _roots = roots;
        _lifecycle = lifecycle; _resources = resources; _application = application; _desktop = desktop;
        _inheritedOrigin = inheritedOrigin; _ownedDesktop = ownedDesktop; _scheduling = scheduling;
        _window = window;
        _logger = logger;
    }

    internal async Task<HostRestartReply> RestartAsync(Guid operationId, CancellationToken cancellation)
    {
        await _serial.WaitAsync(cancellation);
        var stage = "admission";
        try
        {
            if (_stopping || operationId == Guid.Empty || _flight is { IsCompleted: false } || _handoff is not null || _restart is not null)
                throw Invalid();
            if (_desktop is not null && _ownedDesktop is null) throw new IOException("maintenance_desktop_family_proof_required");
            var origin = _inheritedOrigin ?? (_ownedOrigin ??= MaintenanceStartupOrigin.CaptureUnjobbedProcess());
            stage = "drain";
            _drain = _lease.PublishExecution(() => _lifecycle.BeginCompletionDrain(operationId, onlyWhenIdle: true, drainReferences: true)) ?? throw Invalid();
            try
            {
                cancellation.ThrowIfCancellationRequested();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_application.ApplicationStopping);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                stage = "reservation";
                while (!_drain.TryReserve())
                {
                    cancellation.ThrowIfCancellationRequested();
                    await Task.Delay(25, deadline.Token);
                }
                cancellation.ThrowIfCancellationRequested();
                if (!_drain.TryEnterEffect()) throw Invalid();
                stage = "payload";
                _restartRoot = _resources.TryCompletePayload(_drain).Lease ?? throw Invalid();
                var saved = new HostSettingsStore(_database).Read().Values;
                var window = _window is null || _window.Mode == UpdateWindowMode.Headless ? _window : _window with
                {
                    Mode = saved.Desktop.Mode == NexusPipeline.Core.Settings.DesktopMode.Desktop
                        ? UpdateWindowMode.Desktop : UpdateWindowMode.Browser
                };
                _restart = new(origin);
                // The accepted handoff closes its requesting renderer before committing the successor.
                stage = "successor";
                await _restart.PrepareAsync(_instance, operationId, saved.Service.WebPort, window, deadline.Token);
                if (_ownedDesktop is not null)
                {
                    stage = "desktop";
                    using var prepared = _resources.PrepareDesktopExit(_drain, _restartRoot);
                    await _ownedDesktop.ExitForMaintenanceAsync(prepared, deadline.Token);
                }
                stage = "handoff";
                await _restart.CommitAsync(deadline.Token);
                _handedOff = true; _stopping = true;
                _application.StopApplication();
                return _restart.Reply;
            }
            catch
            {
                if (_restart is not null && !await _restart.StopAsync())
                    throw new IOException("host_restart_stop_unconfirmed");
                _restart?.Dispose(); _restart = null;
                _restartRoot?.Dispose(); _restartRoot = null;
                if (!_drain.ReleaseFinishedEffect() && !_drain.TryCancel()) throw Invalid();
                _drain = null;
                throw;
            }
        }
        catch (Exception error)
        {
            _logger?.LogWarning("Host restart rejected during {Stage} ({FailureType})", stage, error.GetType().Name);
            if (stage == "drain") _logger?.LogWarning("Host restart activity: {Activity}", _lifecycle.ReadCompletionActivity());
            throw;
        }
        finally { _serial.Release(); }
    }

    internal IReadOnlyList<PendingHostUpdate> Pending() => Store().Pending();

    internal HostUpdateOperation? Read(Guid operationId, string subject)
        => Store().Read(operationId, subject) ?? (subject.StartsWith("local:", StringComparison.Ordinal)
            ? Store().Read(operationId, HostAutomaticUpdates.Subject) : null);

    internal HostUpdateOperation? Cancel(Guid operationId, string subject)
    {
        var store = Store();
        return store.Read(operationId, subject) is not null ? store.Cancel(operationId, subject) :
            subject.StartsWith("local:", StringComparison.Ordinal) ? store.Cancel(operationId, HostAutomaticUpdates.Subject) : null;
    }

    internal async Task BindWindowAsync(NativeClientBinding window, CancellationToken cancellation)
    {
        await _serial.WaitAsync(cancellation);
        try
        {
            if (_stopping || _flight is { IsCompleted: false } || _handoff is not null ||
                Interlocked.CompareExchange(ref _updateWindow, window, null) is not null) throw Invalid();
        }
        finally { _serial.Release(); }
    }

    internal void ReleaseWindow(NativeClientBinding window) => Interlocked.CompareExchange(ref _updateWindow, null, window);

    internal async Task<HostUpdateOperation> StartAsync(Guid operationId, string subject, CancellationToken cancellation,
        NativeClientBinding? window = null)
    {
        await _serial.WaitAsync(cancellation);
        try
        {
            if (_stopping) throw Invalid();
            if (subject.StartsWith("local:", StringComparison.Ordinal) && Store().Read(operationId, subject) is null &&
                Store().Read(operationId, HostAutomaticUpdates.Subject) is not null) subject = HostAutomaticUpdates.Subject;
            var current = Read(operationId, subject) ?? throw new IOException("maintenance_operation_missing");
            if (current.State is HostUpdateState.Completed or HostUpdateState.Cancelled) return current;
            if (Volatile.Read(ref _updateWindow) is { } bound && !ReferenceEquals(bound, window)) throw Invalid();
            if (_handoff?.Plan.OperationId == operationId) return current;
            if (_flight is { IsCompleted: false } || current.State != HostUpdateState.Pending) throw Invalid();
            if (_desktop is not null && _ownedDesktop is null) throw new IOException("maintenance_desktop_family_proof_required");
            if (_trust is null) throw new IOException("maintenance_release_trust_unavailable");
            var store = Store();
            var origin = _inheritedOrigin ?? (_ownedOrigin ??= MaintenanceStartupOrigin.CaptureUnjobbedProcess());
            _drain = _lease.PublishExecution(() =>
            {
                var idle = current.Timing == HostUpdateTiming.WhenIdle;
                if (idle && _scheduling?.BlocksAutomaticUpdate(TimeSpan.FromMinutes(5)) == true) return null;
                return _lifecycle.BeginCompletionDrain(operationId, onlyWhenIdle: idle);
            }) ?? throw Invalid();
            _operationId = operationId; _subject = subject;
            try
            {
                if (!_drain.TryReserve() || !_drain.TryEnterEffect()) throw Invalid();
                _handoff = store.BeginHandoff(operationId, subject, _drain, cancellation);
                var accepted = Read(operationId, subject) ?? throw Invalid();
                var windowExit = window is null ? Task.CompletedTask : WaitForWindowAsync(window);
                _flight = Task.Run(() => CloseDesktopAsync(origin, windowExit));
                return accepted;
            }
            catch
            {
                await AbortUnhandedAsync();
                throw;
            }
        }
        finally { _serial.Release(); }
    }

    private async Task WaitForWindowAsync(NativeClientBinding window)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_application.ApplicationStopping);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await window.WaitForExitAsync(deadline.Token);
    }

    private async Task CloseDesktopAsync(MaintenanceStartupOrigin origin, Task windowExit)
    {
        try
        {
            await windowExit;
            _bootstrap = MaintenanceBootstrapPayload.Prepare(_handoff ?? throw Invalid(), _lease, _database, _contracts, _roots,
                new(_contracts, _trust!.Policy), _application.ApplicationStopping);
            if (_ownedDesktop is not null)
            {
                using var prepared = _resources.PrepareDesktopExit(_drain!, _handoff!.Root);
                await _ownedDesktop.ExitForMaintenanceAsync(prepared, _application.ApplicationStopping);
            }
            var handoff = _handoff ?? throw Invalid();
            var bootstrap = _bootstrap ?? throw Invalid();
            _ready = new(handoff, _lease, bootstrap);
            _helper = new(origin, _ => { });
            var options = new MaintenanceEntryArguments(_instance.RuntimeRoot, _roots.HostPayloadRoot, _operationId,
                handoff.HandoffId, _ready.PipeName, _helper.Name);
            var writerId = Guid.Empty;
            // The bootstrap tree can exceed Win32's current-directory limit; maintenance uses absolute paths.
            var root = _helper.Start(bootstrap.HelperPath, options.ToArguments("maintenance-apply"), Path.GetPathRoot(bootstrap.HelperPath)!,
                _instance.InstanceId, handoff.HandoffId,
                actual => writerId = MaintenanceRegisteredActor.RegisterSuspended(handoff, _lease, bootstrap, _helper, actual));
            await ObserveHelperAsync(root, writerId);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            FailureCode = "maintenance_desktop_or_helper_not_ready";
            try { await AbortUnhandedAsync(); }
            catch (Exception blocked) when (blocked is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { FailureCode = "maintenance_handoff_blocked"; }
        }
    }

    private async Task ObserveHelperAsync(OwnedWorkerIdentity root, Guid writerId)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ready = _ready!.WaitReadyAsync(_helper!, root, writerId, deadline.Token);
            while (!ready.IsCompleted)
            {
                if (await _helper!.AwaitRootAsync(root, TimeSpan.FromMilliseconds(100)) is not { } exit) continue;
                deadline.Cancel();
                try { await ready; }
                catch (Exception stopped) when (stopped is IOException or OperationCanceledException or System.ComponentModel.Win32Exception) { }
                Console.Error.WriteLine("maintenance_helper_exit_" + exit.ToString(System.Globalization.CultureInfo.InvariantCulture));
                throw new IOException("maintenance_helper_exited_before_ready");
            }
            await ready;
            _handedOff = true;
            _application.StopApplication();
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            FailureCode = "maintenance_helper_not_ready";
            try { await AbortUnhandedAsync(); }
            catch (Exception blocked) when (blocked is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { FailureCode = "maintenance_handoff_blocked"; }
        }
    }

    private async Task AbortUnhandedAsync()
    {
        if (_handedOff) throw Invalid();
        if (_helper is not null && !await _helper.StopAsync(TimeSpan.FromSeconds(10))) throw Invalid();
        var operation = _subject is null ? null : Read(_operationId, _subject);
        if (operation is { State: HostUpdateState.Starting, JournalId: { } journal })
        {
            if (new JournalEffectGate(_database).Read(journal).State != EffectGateState.Unentered) throw Invalid();
            var registered = new RecoveryJournalRegistry(_database).ReadJournal(journal);
            var directory = Path.Combine(_instance.RuntimeRoot, "journals", journal.ToString("D"));
            if (!Directory.Exists(directory))
                new PayloadApplyAcceptance(_instance, _contracts, _database, _roots).MaterializeInitial(registered);
            var writers = new JournalWriterStore(_database);
            _ = new PreparedPayloadRecovery(new PreparedPayloadSession(_instance, journal, _contracts, _database, _roots, () =>
            {
                var actual = writers.ObserveForCurrentExecutor(journal);
                return new(_instance.InstanceId, journal, actual.IsComplete, actual.State);
            })).Recover();
        }
        ReleaseInputs();
        if (_drain is { } drain)
        {
            if (!drain.ReleaseFinishedEffect() && !drain.TryCancel()) throw Invalid();
            _drain = null;
        }
    }

    private MaintenancePendingStore Store()
    {
        return new(_instance, _database, _lease, _resources, _contracts, _roots,
            _trust is null ? null : new(_contracts, _trust.Policy));
    }

    private void ReleaseInputs()
    {
        _ready?.Dispose(); _ready = null;
        _helper?.Dispose(); _helper = null;
        _bootstrap?.Dispose(); _bootstrap = null;
        _handoff?.Dispose(); _handoff = null;
        _ownedOrigin?.Dispose(); _ownedOrigin = null;
    }

    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        if (_flight is { } flight) await flight.WaitAsync(TimeSpan.FromSeconds(40));
        if (!_handedOff && _handoff is not null) await AbortUnhandedAsync();
        if (!_handedOff && _restart is not null && !await _restart.StopAsync())
            throw new IOException("host_restart_stop_unconfirmed");
        _restart?.Dispose(); _restartRoot?.Dispose();
        ReleaseInputs(); _drain?.Dispose(); _serial.Dispose();
    }
    private static IOException Invalid() => new("maintenance_handoff_unavailable");
}
