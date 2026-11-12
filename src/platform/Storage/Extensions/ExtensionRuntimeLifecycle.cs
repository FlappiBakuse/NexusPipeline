using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
public sealed class ExtensionRuntimeLifecycle : IExtensionRuntimeLifecycle, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ContractReader _contracts;
    private readonly PayloadApplyRoots _roots;
    private readonly ResourceLeaseManager _resources;
    private readonly RecoveryIndex _recovery;
    private readonly InstalledActivationService? _activation;
    private readonly Dictionary<string, string> _failures = new(StringComparer.Ordinal);
    private Task? _start;
    private bool _stopping;
    private InstalledPortDependencies? _ports;
    internal ExtensionActivator Activator { get; }
    internal ExtensionCatalogStore? Catalog { get; }
    internal ExtensionReleaseTrust? ReleaseTrust { get; }
    public bool HasReleaseTrust => Catalog is not null;

    public ExtensionRuntimeLifecycle(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ContractReader contracts, PayloadApplyRoots roots, ResourceLeaseManager resources, RecoveryIndex recovery,
        ExecutionProviderRegistry providers, ExtensionReleaseTrust? releaseTrust = null)
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            !resources.UsesRecoveryIndex(recovery)) throw new IOException("extension_runtime_instance_unbound");
        _instance = instance; _database = database; _lease = lease; _contracts = contracts;
        _roots = roots; _resources = resources; _recovery = recovery;
        Activator = new(contracts, lease, resources, providers);
        ReleaseTrust = releaseTrust;
        if (releaseTrust is not null)
        {
            Catalog = new(instance, database, lease, resources, contracts, new(contracts, releaseTrust.Policy));
            _activation = new(instance, database, lease, contracts, roots, resources, recovery, Catalog, Activator);
        }
    }

    public void ConfigurePorts(InstalledPortDependencies dependencies)
    {
        lock (_gate)
        {
            if (_start is not null || _stopping) throw new IOException("extension_runtime_already_started");
            _ = new InstalledPortFactory(_instance, _database, _lease, _resources, Activator, _contracts, _recovery, dependencies);
            _ports = dependencies;
        }
    }

    public ITaskAdapterPort CreateAdapterPort(SecretAccessPolicy secrets)
    {
        lock (_gate)
        {
            if (_start is not null || _stopping) throw new IOException("extension_runtime_already_started");
            if (!ReferenceEquals(_ports?.SecretAuthorization, secrets)) throw new IOException("adapter_secret_policy_owner_unbound");
            return new TaskAdapterService(_instance, _database, _lease, _resources, _contracts, _roots, Activator, secrets);
        }
    }

    public IReadOnlyList<ExtensionRuntimeState> ReadStates()
    {
        _lease.VerifyHeld(_instance);
        using var identity = InstanceReadScope.Hold(_instance, _contracts);
        using var reads = DatabaseReadScope.Hold(_database.DatabasePath);
        using var paths = DirectoryAttributeScope.Hold(_instance);
        using var blobs = BlobReadScope.Hold(_instance);
        var settings = new HostSettingsStore(_database).Read();
        var active = Activator.ReadPublishedScopes().ToDictionary(item => item.ExtensionId, StringComparer.Ordinal);
        var result = new List<ExtensionRuntimeState>();
        foreach (var id in ReadInstallationIds())
        {
            var saved = new ExtensionInstalledEvidence(_instance, _database, _contracts, _roots).ReadCurrent(id)
                ?? throw new IOException("extension_runtime_installation_missing");
            var desired = settings.Values.Extensions.SingleOrDefault(item => item.ExtensionId == id)?.Enabled == true;
            string? failure;
            lock (_gate) _failures.TryGetValue(id, out failure);
            if (desired && saved.Metadata.IsInstalled && !HasReleaseTrust) failure = "extension_release_trust_unprovisioned";
            var manifest = saved.Metadata.IsInstalled ? ExtensionDetailsReader.ReadListing(saved, _contracts) : null;
            result.Add(new(id, saved.Metadata.Revision, saved.Metadata.IsInstalled, desired,
                active.TryGetValue(id, out var scope) ? scope.ActivationId : null, failure)
                { PackageVersion = manifest?.PackageVersion, Kind = manifest?.Kind, ArtifactName = manifest?.ArtifactName,
                    Capabilities = manifest?.Capabilities ?? [], Metadata = ExtensionListingMetadata.Read(_database, saved,
                        ExtensionMetadataReader.Read(manifest?.Metadata)) });
        }
        return result.AsReadOnly();
    }

    public async Task<ExtensionRuntimeState> ReconcileAsync(ExtensionRuntimeSelection selection,
        CancellationToken acceptedHostCancellation)
    {
        ArgumentNullException.ThrowIfNull(selection); ExtensionIdentifiers.Require(selection.ExtensionId);
        if (selection.ExpectedInstalledRevision is < 1 or > 9007199254740991 ||
            selection.ExpectedSettingsRevision is < 0 or > 9007199254740991)
            throw new ArgumentException("extension_runtime_selection_invalid");
        _lease.VerifyHeld(_instance); acceptedHostCancellation.ThrowIfCancellationRequested();
        lock (_gate) if (_stopping) throw new IOException("extension_runtime_stopping");
        ExtensionProjectionPublication.PublishIfDirty(_instance, _database, _lease, _contracts, _roots, _resources, _recovery);
        var installed = new ExtensionInstalledEvidence(_instance, _database, _contracts, _roots).ReadCurrent(selection.ExtensionId)
            ?? throw new IOException("extension_not_installed");
        var intent = new HostSettingsStore(_database).ReadExtensionIntent(selection.ExtensionId, false);
        if (installed.Metadata.Revision != selection.ExpectedInstalledRevision || intent.SettingsRevision != selection.ExpectedSettingsRevision)
            throw new IOException("extension_runtime_revision_conflict");
        if (intent.DesiredEnabled && (!installed.Metadata.IsInstalled || _activation is null))
            throw new IOException(_activation is null ? "extension_release_trust_unprovisioned" : "extension_not_installed");
        IReadOnlyList<ResourceClaim> claims = installed.Metadata.IsInstalled ? InstalledActivationOrigin.Claims(_instance, installed) :
            [new("extension:" + selection.ExtensionId, ResourceClaimMode.Mutation), new("settings:host", ResourceClaimMode.Reference),
                new("blobs:content", ResourceClaimMode.Reference)];
        using var root = await ReservePreferenceAsync(claims, acceptedHostCancellation).ConfigureAwait(false);
        var fresh = new ExtensionInstalledEvidence(_instance, _database, _contracts, _roots).ReadCurrent(selection.ExtensionId);
        if (fresh?.Metadata != installed.Metadata || new HostSettingsStore(_database)
                .ReadExtensionIntent(selection.ExtensionId, false) != intent) throw new IOException("extension_runtime_revision_conflict");
        if (intent.DesiredEnabled && Activator.ReadPublishedScopes().Any(item => item.ExtensionId == selection.ExtensionId))
            return ReadStates().Single(item => item.ExtensionId == selection.ExtensionId);
        using var inventory = await Activator.ReserveForApplyAsync(root, selection.ExtensionId, acceptedHostCancellation).ConfigureAwait(false);
        if (intent.DesiredEnabled)
        {
            try { _ = await _activation!.ActivateReservedAsync(root, inventory, selection.ExtensionId, acceptedHostCancellation).ConfigureAwait(false); }
            catch
            {
                lock (_gate) _failures[selection.ExtensionId] = "extension_preference_activation_failed";
                throw;
            }
        }
        lock (_gate) _failures.Remove(selection.ExtensionId);
        return ReadStates().Single(item => item.ExtensionId == selection.ExtensionId);
    }

    private async Task<ResourceOperationLease> ReservePreferenceAsync(IReadOnlyList<ResourceClaim> claims, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return (await _resources.AcquireAsync(Guid.NewGuid(), claims, deadline.Token).ConfigureAwait(false)).Lease
                ?? throw new IOException("extension_preference_resources_unavailable");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new IOException("extension_preference_resources_unavailable"); }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_stopping) throw new IOException("extension_runtime_stopping");
            return _start ??= StartCoreAsync(cancellationToken);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        ExtensionProjectionPublication.PublishIfDirty(_instance, _database, _lease, _contracts, _roots, _resources, _recovery);
        if (_activation is null) return;
        var selected = ReadEnabledInstallations();
        var executor = _lease.ExecutorIdentity;
        await Parallel.ForEachAsync(selected, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken
        }, async (id, cancellation) =>
        {
            try { _ = await _activation.ActivateAsync(id, cancellation, executor).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                lock (_gate) _failures[id] = "extension_startup_activation_failed";
            }
        }).ConfigureAwait(false);
    }

    private string[] ReadEnabledInstallations()
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        // Larger installation evidence starts earlier so expensive activation does not occupy the final slot alone.
        var selected = HostSettingsRows.ReadCurrent(connection, transaction).Values.Extensions
            .Where(item => item.Enabled)
            .Select(item => ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, item.ExtensionId))
            .OfType<ExtensionInstalledMetadata>().Where(item => item.IsInstalled)
            .OrderByDescending(item => item.Footprint.SizeBytes).Select(item => item.ExtensionId).ToArray();
        transaction.Commit();
        return selected;
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(120)) throw new ArgumentOutOfRangeException(nameof(timeout));
        Task start;
        lock (_gate) { _stopping = true; start = _start ?? Task.CompletedTask; }
        var close = Activator.StopAllAsync(timeout);
        await Task.WhenAll(start, close).WaitAsync(timeout).ConfigureAwait(false);
    }

    private IReadOnlyList<string> ReadInstallationIds()
    {
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT extension_id FROM extension_installations ORDER BY extension_id LIMIT 257;";
        using var row = command.ExecuteReader(); var result = new List<string>();
        while (row.Read()) result.Add(row.GetString(0));
        if (result.Count > 256) throw new IOException("extension_runtime_installation_capacity");
        return result.AsReadOnly();
    }

    public ValueTask DisposeAsync() => new(StopAsync(TimeSpan.FromSeconds(10)));
}
