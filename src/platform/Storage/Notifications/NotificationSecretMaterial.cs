using System.Runtime.Versioning;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Platform.Storage.Notifications;

[SupportedOSPlatform("windows")]
internal sealed class NotificationSecretMaterial : IDisposable
{
    private readonly Dictionary<NotificationSecretField, SecretValue> _values = [];

    internal static NotificationSecretMaterial Capture(PreparedDeliverySecrets prepared)
    {
        var result = new NotificationSecretMaterial();
        try
        {
            foreach (var field in Enum.GetValues<NotificationSecretField>())
                if (prepared.IsConfigured(field)) prepared.Use(field, value => result._values.Add(field, new SecretValue(value)));
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal bool Has(NotificationSecretField field) => _values.ContainsKey(field);

    internal void Use(NotificationSecretField field, SecretValueReader callback)
    {
        if (!_values.TryGetValue(field, out var value)) throw new IOException("notification_secret_not_configured");
        value.Use(callback);
    }

    internal string Text(NotificationSecretField field)
    {
        string? result = null;
        Use(field, value => result = new string(value));
        return result ?? throw new IOException("notification_secret_unavailable");
    }

    public void Dispose() { foreach (var value in _values.Values) value.Dispose(); _values.Clear(); }
}
