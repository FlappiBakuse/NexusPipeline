using System.Text.Json.Serialization;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Secrets;

namespace NexusPipeline.Platform.Storage.Notifications;

internal sealed record NotificationDeliveryNetwork(
    [property: JsonRequired] long SettingsRevision,
    [property: JsonRequired] SecretFieldReference ProxyPassword,
    [property: JsonRequired] SecretPayloadIdentity? ProxyPayload);
