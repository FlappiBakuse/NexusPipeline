using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Platform.Storage.Settings;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Host.Lifecycle;

internal sealed record AutomaticUpdateStatus(DateTimeOffset? LastCheckedAtUtc, string? AvailableHostVersion, string? FailureCode, Guid? PendingOperationId = null);

[SupportedOSPlatform("windows")]
internal sealed class HostAutomaticUpdates : IAsyncDisposable
{
    internal const string Subject = "host:automatic-updates";
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly HostSettingsService _settings;
    private readonly HostUpdateService _updates;
    private readonly HostMaintenanceController _maintenance;
    private readonly ExtensionPackageService _packages;
    private readonly ExtensionRuntimeLifecycle _extensions;
    private readonly HashSet<Guid> _startupPending;
    private readonly CancellationTokenSource _stop;
    private readonly TimeProvider _clock;
    private Task? _loop;
    private DateTimeOffset _nextCheck;
    private bool _pendingPluginScan;
    private long _observedSettingsRevision = -1;
    private AutomaticUpdateStatus _status = new(null, null, null);
    internal AutomaticUpdateStatus Status => Volatile.Read(ref _status);

    internal HostAutomaticUpdates(RuntimeInstance instance, StateDatabase database,
        HostSettingsService settings, HostUpdateService updates, HostMaintenanceController maintenance,
        ExtensionPackageService packages, ExtensionRuntimeLifecycle extensions, CancellationToken stopping, TimeProvider? clock = null)
    {
        _instance = instance; _database = database; _settings = settings; _updates = updates;
        _maintenance = maintenance; _packages = packages; _extensions = extensions;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _clock = clock ?? TimeProvider.System;
        _startupPending = maintenance.Pending().Select(item => item.OperationId).ToHashSet();
        _observedSettingsRevision = settings.Read().Saved.Revision;
    }

    internal async Task<bool> ApplyStartupAsync()
    {
        try
        {
            if (await ApplyPendingAsync(true)) return true;
            if (_settings.Read().Effective.Updates is not { CheckEnabled: true, AutoApplyEnabled: true } || !_updates.HasReleaseTrust) return false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            await CheckAsync(startup: true, deadline.Token);
            _nextCheck = _clock.GetUtcNow() + AutomaticUpdatePolicy.CheckInterval;
            if (Status.PendingOperationId is not { } pending) return false;
            return (await _maintenance.StartAsync(pending, Subject, _stop.Token)).State == HostUpdateState.Starting;
        }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { Fail("automatic_startup_update_deadline"); return false; }
        catch (Exception error) when (Expected(error)) { Fail("automatic_startup_update_blocked"); return false; }
    }

    internal void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("automatic_updates_already_started");
        _loop = RunLoopAsync(TickAsync, Fail, _clock, _stop.Token);
    }

    internal static async Task RunLoopAsync(Func<Task<bool>> cycle, Action<string> fail, TimeProvider clock, CancellationToken stopping)
    {
        try
        {
            await Task.Delay(AutomaticUpdatePolicy.FirstCheckDelay, clock, stopping);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), clock);
            do
            {
                try { if (await cycle()) return; }
                catch (OperationCanceledException) when (!stopping.IsCancellationRequested) { fail("automatic_update_deadline"); }
                catch (Exception error) when (Expected(error)) { fail("automatic_update_check_or_apply_blocked"); }
            } while (await timer.WaitForNextTickAsync(stopping));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private async Task<bool> TickAsync()
    {
        if (await ApplyPendingAsync(false)) return true;
        var saved = _settings.Read();
        if (saved.Saved.Revision != _observedSettingsRevision)
        { _observedSettingsRevision = saved.Saved.Revision; _nextCheck = DateTimeOffset.MinValue; _pendingPluginScan = false; }
        if (_pendingPluginScan && saved.Effective.Updates.PluginAutoUpdateEnabled)
        {
            _pendingPluginScan = false;
            var channel = saved.Effective.Updates.PluginSource == PluginSourceChannel.Stable ? ExtensionStoreChannel.Stable : ExtensionStoreChannel.Develop;
            if (_packages.ReadCached(channel) is { } cached) await UpdatePluginsAsync(saved, cached);
        }
        if (_clock.GetUtcNow() >= _nextCheck)
        {
            _nextCheck = _clock.GetUtcNow() + AutomaticUpdatePolicy.CheckInterval;
            await CheckAsync();
        }
        return false;
    }

    private async Task<bool> ApplyPendingAsync(bool startup)
    {
        foreach (var item in _maintenance.Pending())
        {
            if (item.Subject == Subject && _settings.Read().Effective.Updates is not { CheckEnabled: true, AutoApplyEnabled: true }) continue;
            if (!AutomaticUpdatePolicy.ShouldApplyPending(item.Timing, _startupPending.Contains(item.OperationId), startup)) continue;
            var result = await _maintenance.StartAsync(item.OperationId, item.Subject, _stop.Token);
            if (result.State == HostUpdateState.Starting) return true;
        }
        return false;
    }

    private async Task CheckAsync(bool startup = false, CancellationToken cancellation = default)
    {
        if (!cancellation.CanBeCanceled) cancellation = _stop.Token;
        var settings = _settings.Read();
        if (settings.Effective.Updates.CheckEnabled && _updates.HasReleaseTrust)
        {
            var channel = settings.Effective.Updates.Channel == UpdateChannel.Stable ? HostUpdateChannel.Stable : HostUpdateChannel.Prerelease;
            var feed = await _updates.RefreshAsync(Subject, channel, cancellation);
            var release = feed.Releases.Where(item => AutomaticUpdatePolicy.CompareVersions(item.HostVersion, "0.18.0") > 0 &&
                AutomaticUpdatePolicy.CompareVersions(item.MinReaderVersion, "0.18.0") <= 0)
                .OrderByDescending(item => item.HostVersion, Comparer<string>.Create(AutomaticUpdatePolicy.CompareVersions)).FirstOrDefault();
            Volatile.Write(ref _status, new(DateTimeOffset.UtcNow, release?.HostVersion, null));
            if (release is not null && _settings.Read().Saved.Revision == settings.Saved.Revision &&
                _settings.Read().Effective.Updates is { CheckEnabled: true, AutoApplyEnabled: true })
            {
                var desktop = settings.Effective.Desktop;
                var window = new UpdateWindowIntent(!_maintenance.HasDesktop ? UpdateWindowMode.Headless :
                    desktop.Mode == DesktopMode.Desktop ? UpdateWindowMode.Desktop : UpdateWindowMode.Browser,
                    _maintenance.HasDesktop && desktop.OpenOnStartup, "/");
                var pending = await _updates.PrepareAsync(new(OperationId("host", $"{settings.Saved.Revision}:{feed.Identity}:{release.HostVersion}"),
                    Subject, feed.Identity, release.HostVersion, startup ? HostUpdateTiming.NextStartup : HostUpdateTiming.WhenIdle, window), cancellation);
                Volatile.Write(ref _status, Status with { PendingOperationId = pending.OperationId });
            }
        }
        if (!startup && _settings.Read().Effective.Updates.PluginAutoUpdateEnabled && _packages.HasReleaseTrust)
            await UpdatePluginsAsync(_settings.Read());
    }

    private async Task UpdatePluginsAsync(SettingsRuntimeSnapshot settings, ExtensionCatalogView? cached = null)
    {
        var channel = settings.Effective.Updates.PluginSource == PluginSourceChannel.Stable ? ExtensionStoreChannel.Stable : ExtensionStoreChannel.Develop;
        var catalog = cached ?? await _packages.RefreshAsync(Subject, channel, _stop.Token);
        foreach (var state in _extensions.ReadStates().Where(item => item.IsInstalled))
        {
            if (!_settings.Read().Effective.Updates.PluginAutoUpdateEnabled) return;
            ExtensionInstalledMetadata original;
            using (var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true))
            using (var transaction = connection.BeginTransaction(deferred: true))
            { original = ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, state.ExtensionId) ?? throw new IOException("automatic_extension_missing"); transaction.Commit(); }
            var candidate = catalog.Entries.SingleOrDefault(item => item.ExtensionId == state.ExtensionId);
            if (candidate is null || !_packages.IsAutomaticUpgrade(channel, candidate, original.Revision) ||
                AutomaticUpdatePolicy.CompareVersions(candidate.MinHostVersion, "0.18.0") > 0) continue;
            var current = _settings.Read();
            if (current.Saved.Revision != settings.Saved.Revision || !current.Effective.Updates.PluginAutoUpdateEnabled ||
                current.Effective.Updates.PluginSource != settings.Effective.Updates.PluginSource) return;
            await _packages.ApplyAsync(OperationId("extension", $"{settings.Saved.Revision}:{catalog.Identity}:{state.ExtensionId}:{state.InstalledRevision}"),
                Subject, new(ExtensionApplyAction.Upgrade, state.ExtensionId, state.InstalledRevision, settings.Saved.Revision, catalog.Identity, candidate), _stop.Token);
            _pendingPluginScan = true;
            return;
        }
    }

    private Guid OperationId(string kind, string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"nexus.automatic-update/1:{_instance.InstanceId:D}:{kind}:{value}"))[..16]);
    private void Fail(string code) => Volatile.Write(ref _status, Status with { FailureCode = code });
    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException or System.Text.Json.JsonException or
        ArgumentException or HttpRequestException or System.ComponentModel.Win32Exception or NexusPipeline.Core.Extensions.ExtensionApplyException;

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { if (_loop is not null) await _loop.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { _stop.Dispose(); }
    }
}
