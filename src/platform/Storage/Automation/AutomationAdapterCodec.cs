using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Automation;

internal static class AutomationAdapterCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<ConfigurationSyntax>(JsonNamingPolicy.CamelCase, false) }
    };
    internal static string Encode(ImmutableArray<AutomationAdapterResource> value) => JsonSerializer.Serialize(value, Options);
    internal static ImmutableArray<AutomationAdapterResource> Decode(string text)
    {
        try
        {
            using var parsed = StrictJson.Parse(Encoding.UTF8.GetBytes(text));
            var value = parsed.RootElement.Deserialize<ImmutableArray<AutomationAdapterResource>>(Options);
            if (value.IsDefault || value.Any(resource => resource is null || resource.Access is null)) throw new IOException("automation_adapter_persisted_invalid");
            foreach (var resource in value) _ = ConfigurationPatchPolicy.Freeze(resource.Access);
            if (!JsonElement.DeepEquals(parsed.RootElement, JsonSerializer.SerializeToElement(value, Options))) throw new IOException("automation_adapter_persisted_invalid");
            return value;
        }
        catch (Exception error) when (error is JsonException or ConfigurationException or InvalidOperationException)
        { throw new IOException("automation_adapter_persisted_invalid", error); }
    }
}
