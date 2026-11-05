using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class ExtensionStoreNetwork
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly ResourceLeaseManager _resources;
    private readonly Func<ProxyPreferences> _readProxy;
    private readonly SecretFieldService _secrets;

    internal ExtensionStoreNetwork(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceLeaseManager resources, Func<ProxyPreferences> readProxy, SecretFieldService secrets)
    {
        ArgumentNullException.ThrowIfNull(readProxy); ArgumentNullException.ThrowIfNull(secrets);
        secrets.RequireOwner(instance, database, lease, resources);
        _instance = instance; _database = database; _lease = lease; _resources = resources;
        _readProxy = readProxy; _secrets = secrets;
    }

    internal HttpClient CreateClient()
    {
        _lease.VerifyHeld(_instance);
        var operation = Guid.NewGuid();
        var admission = _resources.TryAcquire(operation,
            [new("settings:host", ResourceClaimMode.Reference), new("secrets:fields", ResourceClaimMode.Reference)]);
        using var root = admission.Lease ?? throw new IOException(admission.Denial == ResourceLeaseDenial.Conflict
            ? "extension_store_network_busy" : "extension_store_network_unavailable");
        var proxy = _readProxy();
        if (proxy != new HostSettingsStore(_database).Read().Values.Proxy) throw new IOException("extension_store_proxy_snapshot_changed");
        using var credential = _secrets.PrepareProxyCredential(root, operation);
        credential.VerifyHeld(_instance.InstanceId, operation);
        HttpClient? client = null;
        if (proxy.Mode == ProxyMode.Http && !string.IsNullOrWhiteSpace(proxy.UserName) && credential.IsConfigured)
            credential.Use(value => { using var material = new SecretValue(value); client = ProbeHttpClient.CreateStore(proxy, material); });
        else client = ProbeHttpClient.CreateStore(proxy, null);
        // The handler owns its credential copy; later saves must not change an already started transfer.
        return client ?? throw new IOException("extension_store_network_unavailable");
    }
}
