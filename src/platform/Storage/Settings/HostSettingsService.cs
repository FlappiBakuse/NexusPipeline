using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Settings;

public sealed class HostSettingsService
{
    private readonly RuntimeInstance _instance;
    private readonly InstanceLease _instanceLease;
    private readonly ResourceLeaseManager _resources;
    private readonly HostSettingsStore _store;
    private readonly HostSettingsRuntime _runtime;
    private readonly StartupRegistrationCoordinator? _startup;
    public HostLogLevel EffectiveLogLevel => _runtime.Read().Effective.LogLevel;

    public HostSettingsService(RuntimeInstance instance, StateDatabase database, InstanceLease instanceLease,
        ResourceLeaseManager resources, SettingsStartupValues startup, StartupRegistrationCoordinator? startupRegistration = null)
    {
        instanceLease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new IOException("settings_service_instance_unbound");
        _instance = instance;
        _instanceLease = instanceLease;
        _resources = resources;
        _startup = startupRegistration;
        _store = new(database);
        _runtime = new(_store.Read(), startup);
    }

    public SettingsRuntimeSnapshot Read()
    {
        _runtime.PublishSaved(_store.Read());
        if (_startup is null) return _runtime.Read();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _runtime.PublishStartupRegistration(_startup.ReadRegisteredEnabled());
        return _runtime.Read() with { StartupEffect = _startup.ReadPendingStatus() };
    }

    public void PublishDesktopMode(DesktopMode mode)
    {
        _instanceLease.VerifyHeld(_instance);
        _runtime.PublishDesktopMode(mode);
    }

    public SettingsSaveResult SaveService(Guid operationId, string serverSubject, long expectedRevision,
        ServicePreferences service, DateTimeOffset acceptedAt)
    {
        _instanceLease.VerifyHeld(_instance);
        ArgumentNullException.ThrowIfNull(service);
        var source = _store.ReadRevision(expectedRevision);
        var change = new HostSettingsChange(expectedRevision, source.Values with { Service = service });
        var identity = HostSettingsStore.Identity(operationId, serverSubject, change);
        return Save(identity, change, acceptedAt);
    }

    public SettingsSaveResult Save(OperationIdentity operation, HostSettingsChange change, DateTimeOffset acceptedAt)
    {
        _instanceLease.VerifyHeld(_instance);
        var replay = _store.Lookup(operation, change);
        if (replay is not null) return replay;
        var claims = SaveClaims(change);
        var admission = _resources.TryAcquire(operation.OperationId, claims);
        using var lease = admission.Lease ?? throw new HostSettingsException(admission.Denial == ResourceLeaseDenial.Conflict
            ? "settings_resource_conflict" : "settings_host_unavailable");
        return SaveReserved(operation, change, acceptedAt, lease);
    }

    public async Task<SettingsSaveResult> SaveAsync(OperationIdentity operation, HostSettingsChange change,
        DateTimeOffset acceptedAt, Action requireAuthorization, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(requireAuthorization);
        _instanceLease.VerifyHeld(_instance); cancellation.ThrowIfCancellationRequested(); requireAuthorization();
        var replay = _store.Lookup(operation, change);
        if (replay is not null) return replay;
        var claims = SaveClaims(change);
        ResourceLeaseAdmission admission;
        if (claims.Any(claim => claim.ResourceKey == "settings:extension-preferences"))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try { admission = await _resources.AcquireAsync(operation.OperationId, claims, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            { throw new HostSettingsException("settings_resource_conflict"); }
        }
        else admission = _resources.TryAcquire(operation.OperationId, claims);
        using var lease = admission.Lease ?? throw new HostSettingsException(admission.Denial == ResourceLeaseDenial.Conflict
            ? "settings_resource_conflict" : "settings_host_unavailable");
        cancellation.ThrowIfCancellationRequested(); requireAuthorization();
        return SaveReserved(operation, change, acceptedAt, lease);
    }

    private SettingsSaveResult SaveReserved(OperationIdentity operation, HostSettingsChange change,
        DateTimeOffset acceptedAt, ResourceOperationLease lease)
    {
        _instanceLease.VerifyHeld(_instance);
        var replay = _store.Lookup(operation, change);
        if (replay is not null) return replay;
        var before = _store.Read();
        if (before.Revision != change.ExpectedRevision) throw new HostSettingsException("settings_revision_conflict");
        var draft = before.Values.AutoStart != change.Values.AutoStart && _startup is not null
            ? PrepareStartup(operation, before, change) : null;
        var result = _store.Commit(operation, change, acceptedAt, draft);
        if (draft is not null) NotifySaved(operation.OperationId);
        _runtime.PublishSaved(_store.Read());
        if (draft is not null) result = ExecuteStartup(operation.OperationId, lease, result);
        return result;
    }

    private IReadOnlyList<ResourceClaim> SaveClaims(HostSettingsChange change)
    {
        var source = _store.ReadRevision(change.ExpectedRevision);
        HostSettingsPolicy.Validate(change.Values);
        var preferencesOnly = source.Values == (change.Values with { Extensions = source.Values.Extensions });
        var claims = new List<ResourceClaim>
            { new("settings:host", preferencesOnly ? ResourceClaimMode.Reference : ResourceClaimMode.Mutation) };
        if (!preferencesOnly)
        {
            if (_startup is not null) claims.Add(new("system:startup", ResourceClaimMode.Mutation));
            return claims;
        }
        claims.Add(new("settings:extension-preferences", ResourceClaimMode.Mutation));
        var before = source.Values.Extensions.ToDictionary(item => item.ExtensionId, StringComparer.Ordinal);
        var after = change.Values.Extensions.ToDictionary(item => item.ExtensionId, StringComparer.Ordinal);
        foreach (var id in before.Keys.Union(after.Keys, StringComparer.Ordinal))
            if (before.GetValueOrDefault(id) != after.GetValueOrDefault(id))
                claims.Add(new("extension:" + id, ResourceClaimMode.Mutation));
        return claims;
    }

    private StartupIntentDraft PrepareStartup(OperationIdentity operation, HostSettingsSnapshot before, HostSettingsChange change)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return _startup!.Prepare(operation, before, change);
    }

    private void NotifySaved(Guid operationId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _startup!.Saved(operationId);
    }

    private SettingsSaveResult ExecuteStartup(Guid operationId, ResourceOperationLease lease, SettingsSaveResult result)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _startup!.SnapshotPublished(operationId);
        var status = _startup.Execute(operationId, lease);
        if (status.State == StartupEffectState.Completed) _runtime.PublishStartupRegistration(status.Enable);
        return result with { StartupEffect = status };
    }
}
