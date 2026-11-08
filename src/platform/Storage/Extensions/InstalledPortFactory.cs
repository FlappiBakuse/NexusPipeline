using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Notifications;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
public sealed class InstalledPortFactory
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly RecoveryIndex _recovery;
    private readonly ExtensionActivator _activator;
    private readonly ContractReader _contracts;
    private readonly InstalledPortDependencies _dependencies;

    public InstalledPortFactory(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceLeaseManager resources, ExtensionActivator activator, ContractReader contracts, RecoveryIndex recovery,
        InstalledPortDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        lease.VerifyHeld(instance); lease.RequireExtensionActivator(activator, resources);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot || !resources.UsesRecoveryIndex(recovery))
            throw new IOException("extension_port_factory_instance_unbound");
        dependencies.Scheduling?.RequireOwner(instance, database, lease, resources);
        dependencies.Secrets?.RequireOwner(instance, database, lease, resources);
        _instance = instance; _database = database; _lease = lease; _resources = resources; _recovery = recovery;
        _activator = activator; _contracts = contracts; _dependencies = dependencies;
        activator.BindPorts(this);
    }

    internal InstalledExtensionConsumption CreateConsumption(PreparedExtensionActivation source, ExtensionScope scope)
    {
        _lease.VerifyHeld(_instance); _lease.RequireExtensionActivator(_activator, _resources);
        source.RequirePortOwner(_instance, _database, _lease, scope);
        return new(_lease, _activator, _resources, scope, source.CaptureConsumptionFacts());
    }

    internal ExtensionActivationContext Create(PreparedExtensionActivation source, ExtensionScope scope,
        TreeReadLease.TreeBorrow files, out InstalledExtensionConsumption consumption)
    {
        _lease.VerifyHeld(_instance); _lease.RequireExtensionActivator(_activator, _resources);
        source.RequirePortOwner(_instance, _database, _lease, scope);
        var facts = source.CaptureConsumptionFacts();
        consumption = new InstalledExtensionConsumption(_lease, _activator, _resources, scope, facts);
        var manifest = source.ReadManifest();
        var unavailable = new UnavailableExtensionPorts();
        var schedules = _dependencies.Scheduling is null ? null :
            _dependencies.Scheduling.CreateSchedulePort(manifest, scope);
        var notifications = _dependencies.Scheduling is null ? null :
            new LazyNotificationPort(_dependencies.Scheduling, manifest, scope, consumption);
        var secrets = _dependencies.Secrets is null || _dependencies.SecretAuthorization is null ? null :
            new ExtensionSecretPort(manifest, scope, _dependencies.SecretAuthorization, _dependencies.Secrets, _contracts);
        var previews = _dependencies.MediaPreviews is null ? null :
            new MediaRegistrationPort(manifest, scope, _dependencies.MediaPreviews, _contracts);
        var screenshots = _dependencies.RunFrames is null || _dependencies.RunMediaAuthorization is null ? null :
            new RunScreenshotPort(manifest, scope, _dependencies.RunFrames, _dependencies.RunMediaAuthorization, _contracts);
        var effects = _dependencies.Scheduling is null || secrets is null ? null : new InstalledWorkEffects(_instance, _database, _contracts, _resources,
            consumption, _dependencies.Scheduling, secrets, _dependencies.Secrets!, manifest.ExtensionId,
            manifest.Capabilities.Contains("schedule.register", StringComparer.Ordinal));
        return new(
            schedules is null ? unavailable : new InstalledSchedulePort(source, consumption, schedules, _dependencies.Scheduling!, scope),
            secrets is null ? unavailable : new InstalledSecretPort(consumption, secrets),
            notifications is null ? unavailable : new InstalledNotificationPort(consumption, notifications),
            previews is null ? unavailable : new InstalledMediaPort(source, consumption, previews),
            screenshots is null ? unavailable : new InstalledScreenshotPort(consumption, screenshots),
            source.CaptureAssets(scope, consumption, files),
            new InstalledRuntimeOutputs(_instance, source, scope, consumption),
            new InstalledDocumentPort(_database, _resources, consumption, manifest.ExtensionId,
                manifest.Permissions.FileAccess?.Contains("instance-data", StringComparer.Ordinal) == true),
            _dependencies.CreateHttpClient is null ? null : new InstalledHttpPort(consumption, scope, manifest, _dependencies.CreateHttpClient),
            new InstalledImagePort(_database, _resources, consumption, manifest.ExtensionId,
                manifest.Permissions.FileAccess?.Contains("instance-data", StringComparer.Ordinal) == true),
            new InstalledDesktopPort(consumption, _dependencies.Desktop, _dependencies.NativeClients, _dependencies.BrowserLogins, scope, manifest),
            new InstalledActivationPort(consumption, scope, source.Root.WhenReleased,
                () => facts.PublishCurrent(_resources, _recovery)),
            secrets is null ? null : new InstalledSecretDocuments(consumption,
                new SecretDocumentService(_instance, _database, _lease, _resources, _recovery, _contracts,
                    secretService: _dependencies.Secrets), secrets, manifest.ExtensionId,
                manifest.Permissions.FileAccess?.Contains("instance-data", StringComparer.Ordinal) == true, effects),
            new InstalledNotificationTargets(consumption,
                new NotificationCatalogService(_instance, _database, _lease, _resources, _recovery, _contracts),
                manifest.Capabilities.Contains("notification.submit", StringComparer.Ordinal)),
            _dependencies.Scheduling is null ? null : new InstalledWorkTasks(consumption, _dependencies.Scheduling, scope,
                manifest.Capabilities.Contains("schedule.register", StringComparer.Ordinal)),
            effects, new InstalledCompilerPort(source, consumption, scope, _activator.Compilers, manifest),
            new InstalledEmulatorPort(source, consumption, scope, _activator.Emulators, manifest));
    }

}
