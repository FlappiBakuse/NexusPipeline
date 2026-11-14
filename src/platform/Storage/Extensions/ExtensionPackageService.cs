using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
public sealed class ExtensionPackageService : IExtensionPackages, IAsyncDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly InstanceLease _lease;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly PayloadApplyRoots _roots;
    private readonly ResourceLeaseManager _resources;
    private readonly RecoveryIndex _recovery;
    private readonly ExtensionCatalogStore? _catalog;
    private readonly StoreHttpTransport? _transport;
    private readonly ExtensionApplyCoordinator _applies;
    private readonly LocalExtensionInstaller _local;
    private readonly ExtensionWorkSweep _workCleanup;
    private readonly ExtensionArchiveCleanup _archiveCleanup;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private Task _cleanupLoop = Task.CompletedTask;
    public bool HasReleaseTrust => _catalog is not null;

    public ExtensionPackageService(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ContractReader contracts, PayloadApplyRoots roots, ResourceLeaseManager resources, RecoveryIndex recovery,
        ExtensionRuntimeLifecycle runtime, Func<ProxyPreferences> readProxy, SecretFieldService secrets)
        : this(instance, database, lease, contracts, roots, resources, recovery, runtime,
            runtime.ReleaseTrust is null ? null : new ExtensionStoreNetwork(instance, database, lease, resources, readProxy, secrets).CreateClient) { }

    internal ExtensionPackageService(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ContractReader contracts, PayloadApplyRoots roots, ResourceLeaseManager resources, RecoveryIndex recovery,
        ExtensionRuntimeLifecycle runtime, Func<HttpClient>? createClient)
    {
        lease.VerifyHeld(instance); lease.RequireExtensionActivator(runtime.Activator, resources);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot ||
            !resources.UsesRecoveryIndex(recovery)) throw new IOException("extension_package_service_unbound");
        _instance = instance; _database = database; _lease = lease; _contracts = contracts;
        _roots = roots; _resources = resources; _recovery = recovery; _catalog = runtime.Catalog;
        if (_catalog is not null)
        {
            _transport = new(contracts, runtime.ReleaseTrust!.Policy, createClient ?? throw new IOException("extension_transport_required"));
        }
        else if (createClient is not null) throw new IOException("extension_release_trust_unprovisioned");
        _applies = new(new(instance, database, lease, contracts, roots, resources, recovery,
            _catalog, runtime.Activator, _transport));
        _local = new(instance, database, lease, contracts, roots, resources, recovery, runtime);
        _workCleanup = new(database, roots, recovery, runtime.Activator,
            new(instance, database, lease, contracts, roots, resources, recovery, runtime.Activator));
        _archiveCleanup = new(instance, database, lease, contracts, roots, resources, recovery);
    }

    internal void StartCleanup() => _cleanupLoop = RunCleanupAsync(_stopping.Token);

    private async Task RunCleanupAsync(CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var executor = _lease.ExecutorIdentity;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false))
            {
                await _workCleanup.SweepAsync(1, cancellation).ConfigureAwait(false);
                _archiveCleanup.Sweep(cancellation, executor);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    internal async Task InstallLocalAsync(string directory)
    {
        RequireAvailable();
        await _refresh.WaitAsync(_stopping.Token).ConfigureAwait(false);
        try
        {
            await _local.InstallAllAsync(directory, _stopping.Token).ConfigureAwait(false);
        }
        finally { _refresh.Release(); }
    }

    public ExtensionCatalogView? ReadCached(ExtensionStoreChannel channel)
    {
        RequireAvailable(); var saved = _catalog!.ReadCached(channel);
        return saved is null ? null : View(saved);
    }

    public async Task<ExtensionCatalogDetails?> CatalogDetailsAsync(ExtensionCatalogIdentity identity,
        string extensionId, CancellationToken cancellationToken)
    {
        RequireAvailable();
        if (identity is null || !Enum.IsDefined(identity.Channel) ||
            extensionId is not { Length: >= 1 and <= 80 } || extensionId[0] is not (>= 'a' and <= 'z') ||
            extensionId[^1] == '-' || extensionId.Contains("--", StringComparison.Ordinal) ||
            extensionId.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            throw new ArgumentException("extension_catalog_details_invalid");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        var catalog = _catalog!.ReadCached(identity.Channel);
        if (catalog is null || catalog.Identity != identity) return null;
        var entry = catalog.Entries.SingleOrDefault(item => item.ExtensionId == extensionId);
        if (entry is null) return null;
        using var content = _resources.TryAcquire(Guid.NewGuid(), [new("blobs:content", ResourceClaimMode.Reference)]).Lease
            ?? throw new IOException("extension_catalog_details_unavailable");
        var reference = new BlobReference($"blobs/content/{entry.ArtifactSha256}.bin", entry.ArtifactBytes, entry.ArtifactSha256);
        using var cached = File.Exists(Path.Combine(_instance.RuntimeRoot, reference.RelativePath))
            ? new ImmutableBlobStore(_instance, _contracts).OpenVerified(reference) : null;
        using var download = cached is null ? await OwnedExtensionDownload.Receive(_instance, _lease, _resources,
            Guid.NewGuid(), entry, (destination, token) => _transport!.Artifact(catalog, extensionId, destination, token),
            cancellation.Token).ConfigureAwait(false) : null;
        using var input = cached ?? download!.OpenRead();
        using var archive = ExtensionPackageArchive.Open(input, cancellation.Token);
        var manifestBytes = archive.ReadManifest(cancellation.Token);
        var manifest = new ExtensionManifestAdmission(_contracts).ReadCurrent(manifestBytes, extensionId);
        VerifiedExtensionPackage.RequireManifest(entry, manifest, manifestBytes, archive.Files);
        VerifiedExtensionPackage.RequireContents(manifest, archive);
        using var parsed = StrictJson.Parse(manifestBytes);
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "README.md", "README.en.md", "README.zh-CN.md",
            "CHANGELOG.md", "CHANGELOG.en.md", "CHANGELOG.zh-CN.md" })
            if (archive.ReadDocument(name, cancellation.Token) is { } bytes)
                documents.Add(name, new UTF8Encoding(false, true).GetString(bytes));
        cancellation.Token.ThrowIfCancellationRequested();
        var current = _catalog.ReadCached(identity.Channel);
        if (current?.Identity != identity || current.Entries.SingleOrDefault(item => item.ExtensionId == extensionId) != entry)
            throw new IOException("extension_catalog_details_changed");
        return new(identity, extensionId, entry.PackageVersion, parsed.RootElement.Clone(), documents);
    }

    internal bool IsAutomaticUpgrade(ExtensionStoreChannel channel, ExtensionCatalogEntry candidate, long expectedInstalledRevision)
    {
        RequireAvailable();
        var original = new ExtensionInstalledEvidence(_instance, _database, _contracts, _roots).ReadCurrent(candidate.ExtensionId);
        if (original is null || !original.Metadata.IsInstalled || original.Metadata.Revision != expectedInstalledRevision) return false;
        var package = original.Metadata.Package;
        return AutomaticUpdatePolicy.ShouldUpgradeExtension(candidate.PackageVersion, package.PackageVersion,
            candidate.ArtifactSha256 == package.ArchiveSha256 && candidate.ManifestSha256 == package.ManifestSha256,
            channel == ExtensionStoreChannel.Develop, original.Operation.Source.Package?.Catalog.Identity.Channel == ExtensionStoreChannel.Develop);
    }

    public async Task<ExtensionCatalogView> RefreshAsync(string subject, ExtensionStoreChannel channel,
        CancellationToken cancellationToken)
    {
        RequireAvailable();
        if (subject is not { Length: >= 1 and <= 256 } || subject.Any(char.IsControl) || !Enum.IsDefined(channel))
            throw new ArgumentException("extension_catalog_refresh_invalid");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        await _refresh.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            RequireAvailable();
            var catalog = await _transport!.Catalog(channel, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            Publish();
            _ = _catalog!.Accept(Guid.NewGuid(), subject, catalog);
            return View(catalog);
        }
        finally { _refresh.Release(); }
    }

    public async Task<ExtensionApplyAccepted> ApplyAsync(Guid operationId, string subject, ExtensionApplyCommand request,
        CancellationToken clientCancellation)
    {
        _lease.VerifyHeld(_instance); ArgumentNullException.ThrowIfNull(request);
        var action = request.Action switch
        {
            ExtensionApplyAction.Install => PayloadAction.Install, ExtensionApplyAction.Upgrade => PayloadAction.Upgrade,
            ExtensionApplyAction.Remove => PayloadAction.Remove, _ => throw new ArgumentException("extension_apply_selection_invalid")
        };
        var frozen = new ExtensionApplyRequest(action, request.ExtensionId, request.ExpectedInstalledRevision,
            request.ExpectedSettingsRevision, request.Catalog, request.Candidate);
        if (_lease.PublishExecution(() => new PayloadAcceptanceLookup(_instance, _database, _contracts, _roots)
            .ReadAccepted(operationId, subject, frozen)) is { } replay)
            return new(replay.Acceptance.OperationId, replay.JournalId, replay.Acceptance.Status);
        if (action == PayloadAction.Remove) RequireRunning(); else RequireAvailable();
        clientCancellation.ThrowIfCancellationRequested();
        var flight = _applies.Start(operationId, subject, frozen);
        var accepted = await flight.WaitAcceptedAsync(clientCancellation).ConfigureAwait(false);
        return new(accepted.Acceptance.OperationId, accepted.JournalId, accepted.Acceptance.Status);
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(120)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _stopping.Cancel();
        await Task.WhenAll(_applies.StopAsync(timeout), _cleanupLoop).WaitAsync(timeout).ConfigureAwait(false);
        if (!await _refresh.WaitAsync(timeout).ConfigureAwait(false)) throw new TimeoutException("extension_refresh_stop_pending");
        _refresh.Release();
    }

    private void RequireAvailable()
    {
        RequireRunning();
        if (!HasReleaseTrust) throw new IOException("extension_release_trust_unprovisioned");
    }
    private void RequireRunning()
    {
        _lease.VerifyHeld(_instance);
        if (_stopping.IsCancellationRequested) throw new IOException("extension_package_service_stopping");
    }
    private void Publish() => ExtensionProjectionPublication.PublishIfDirty(_instance, _database, _lease,
        _contracts, _roots, _resources, _recovery);
    private static ExtensionCatalogView View(VerifiedExtensionCatalog value) => new(value.Identity, value.PublishedAt, value.Entries);
    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        finally { try { _workCleanup.Dispose(); } finally { _transport?.Dispose(); } }
    }
}
