using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Control;
using NexusPipeline.Core.Settings;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Platform.Storage.Settings;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Host.Control.Settings;

[SupportedOSPlatform("windows")]
internal sealed class SettingsUseCases(HostSettingsService settings)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) }
    };
    internal SettingsRuntimeSnapshot Read(AuthenticatedControlRequest request)
    { request.Require(ControlCapability.SettingsRead); return settings.Read(); }
    internal Task<SettingsSaveResult> SaveAsync(AuthenticatedControlRequest request, JsonElement root, CancellationToken cancellation)
    {
        request.Require(ControlCapability.SettingsWrite);
        var operationId = ControlRequestBody.Uuid(root.GetProperty("operationId"));
        var values = root.GetProperty("values").Deserialize<HostSettings>(Json)
            ?? throw new ControlRequestException(400, "settings_invalid");
        HostSettingsPolicy.Validate(values);
        var change = new HostSettingsChange(root.GetProperty("expectedRevision").GetInt64(), values);
        request.Require(ControlCapability.SettingsWrite);
        return settings.SaveAsync(HostSettingsStore.Identity(operationId, request.SubjectId, change), change, DateTimeOffset.UtcNow,
            () => request.Require(ControlCapability.SettingsWrite), cancellation);
    }
}
