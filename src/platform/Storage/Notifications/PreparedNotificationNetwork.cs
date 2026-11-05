using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
internal sealed class PreparedNotificationNetwork : IDisposable
{
    private readonly PreparedProxyCredential _credential;
    private readonly Guid _instanceId, _operationId;
    private readonly IDisposable? _held;
    private readonly ProxyPreferences _proxy;
    internal NotificationDeliveryNetwork View { get; }

    private PreparedNotificationNetwork(Guid instance, Guid operation, ProxyPreferences proxy,
        PreparedProxyCredential credential, NotificationDeliveryNetwork view, IDisposable? held)
    {
        _instanceId = instance; _operationId = operation; _proxy = proxy;
        _credential = credential; View = view; _held = held;
    }

    internal static PreparedNotificationNetwork Capture(RuntimeInstance instance, StateDatabase database,
        ResourceOperationLease root, Guid operation, SecretFieldService secrets, NotificationDeliveryNetwork expected)
    {
        var credential = secrets.PrepareProxyCredential(root, operation);
        IDisposable? held = null;
        try
        {
            credential.VerifyHeld(instance.InstanceId, operation);
            var settings = new HostSettingsStore(database).Read();
            SecretPayloadIdentity? payload = null;
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                if (credential.Reference.SecretId is { } id) payload = SecretFieldStore.ReadPayload(connection, transaction, id);
                var view = new NotificationDeliveryNetwork(settings.Revision, credential.Reference, payload);
                if (view != expected) throw new IOException("notification_delivery_network_unbound");
                DeliveryNetworkEvidence.RequireCurrent(connection, transaction, instance.InstanceId, view);
                transaction.Commit();
                if (payload is not null) held = new SecretPayloadFiles(instance, new UserSecretProtector()).HoldVerified(payload);
                return new(instance.InstanceId, operation, settings.Values.Proxy, credential, view, held);
            }
        }
        catch { held?.Dispose(); credential.Dispose(); throw; }
    }

    internal HttpClient CreateHttpClient()
    {
        _credential.VerifyHeld(_instanceId, _operationId);
        HttpClient? client = null;
        if (_proxy.Mode == ProxyMode.Http && !string.IsNullOrWhiteSpace(_proxy.UserName) && _credential.IsConfigured)
            _credential.Use(value => { using var material = new SecretValue(value); client = ProbeHttpClient.Create(_proxy, material); });
        else client = ProbeHttpClient.Create(_proxy, null);
        return client ?? throw new IOException("notification_proxy_unavailable");
    }

    public void Dispose() { _held?.Dispose(); _credential.Dispose(); }
}
