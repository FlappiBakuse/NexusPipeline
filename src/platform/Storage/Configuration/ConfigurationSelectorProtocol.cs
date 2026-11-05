using System.Collections.Immutable;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;

namespace NexusPipeline.Platform.Storage.Configuration;

internal static class ConfigurationSelectorProtocol
{
    internal static ConfigurationDocumentAccess Read(ConfigurationSelectorAccess access) => ConfigurationPatchPolicy.Freeze(new ConfigurationDocumentAccess(
        access.Read.Select(item => new ConfigurationAccessGrant(Read(item.Selector, true), item.Descendants)).ToImmutableArray(),
        access.Write.Select(item => new ConfigurationAccessGrant(Read(item.Selector, true), item.Descendants)).ToImmutableArray(),
        access.Protected.Select(item => Read(item, true)).ToImmutableArray()));

    internal static ConfigurationSelectorAccess Write(ConfigurationDocumentAccess access)
    {
        access = ConfigurationPatchPolicy.Freeze(access);
        ConfigurationSelectorGrant Grant(ConfigurationAccessGrant item) => new() { Selector = Write(item.Selector, true), Descendants = item.Descendants };
        return new() { Read = access.Read.Select(Grant).ToArray(), Write = access.Write.Select(Grant).ToArray(),
            Protected = access.Protected.Select(item => Write(item, true)).ToArray() };
    }

    internal static ConfigurationTaskSource ReadTaskSource(JsonElement value)
    {
        ConfigurationSelector Selector(string name) => Read(value.GetProperty(name).EnumerateArray().ToArray(), true);
        ConfigurationTaskSource source = value.GetProperty("kind").GetString() switch
        {
            "objects" => new ConfigurationObjectTasks(Selector("items"), value.GetProperty("idProperty").GetString()!, value.GetProperty("enabledProperty").GetString()!),
            "map" => new ConfigurationMapTasks(Selector("items")),
            "parallel" => new ConfigurationParallelTasks(Selector("ids"), Selector("states")),
            "selectors" => new ConfigurationSelectorTasks(value.GetProperty("targets").EnumerateArray().Select(target =>
                new ConfigurationTaskTarget(target.GetProperty("taskId").GetString()!,
                    Read(target.GetProperty("selector").EnumerateArray().ToArray(), false))).ToImmutableArray()),
            _ => throw new ConfigurationException("configuration_task_declaration_invalid")
        };
        return source with { IdPrefix = value.TryGetProperty("idPrefix", out var prefix) ? prefix.GetString()! : "" };
    }

    internal static JsonElement WriteTaskSource(ConfigurationTaskSource source) => source switch
    {
        ConfigurationObjectTasks objects => JsonSerializer.SerializeToElement(new { kind = "objects", items = Write(objects.Items, true),
            idProperty = objects.IdProperty, enabledProperty = objects.EnabledProperty, idPrefix = objects.IdPrefix }),
        ConfigurationMapTasks map => JsonSerializer.SerializeToElement(new { kind = "map", items = Write(map.Items, true), idPrefix = map.IdPrefix }),
        ConfigurationParallelTasks parallel => JsonSerializer.SerializeToElement(new { kind = "parallel", ids = Write(parallel.Ids, true),
            states = Write(parallel.States, true), idPrefix = parallel.IdPrefix }),
        ConfigurationSelectorTasks selectors => JsonSerializer.SerializeToElement(new { kind = "selectors", targets = selectors.Targets.Select(target =>
            new { taskId = target.TaskId, selector = Write(target.Selector, false) }).ToArray(), idPrefix = selectors.IdPrefix }),
        _ => throw new ConfigurationException("configuration_task_declaration_invalid")
    };

    internal static ConfigurationSelector Read(IReadOnlyList<JsonElement> steps, bool allowRoot) =>
        ConfigurationPatchPolicy.Freeze(new(steps.Select(step => step.ValueKind switch
        {
            JsonValueKind.String => (ConfigurationSelectorStep)new ConfigurationProperty(step.GetString()!),
            JsonValueKind.Number when step.TryGetInt32(out var index) => new ConfigurationIndex(index),
            JsonValueKind.Object => new ConfigurationMatch(step.GetProperty("match").GetProperty("property").GetString()!,
                step.GetProperty("match").GetProperty("value").GetString()!),
            _ => throw new ConfigurationException("configuration_selector_invalid")
        }).ToImmutableArray()), allowRoot);

    internal static IReadOnlyList<JsonElement> Write(ConfigurationSelector selector, bool allowRoot) =>
        ConfigurationPatchPolicy.Freeze(selector, allowRoot).Steps.Select(Step).ToArray();

    private static JsonElement Step(ConfigurationSelectorStep step) => step switch
    {
        ConfigurationProperty property => JsonSerializer.SerializeToElement(property.Name),
        ConfigurationIndex index => JsonSerializer.SerializeToElement(index.Index),
        ConfigurationMatch match => JsonSerializer.SerializeToElement(new { match = new { property = match.Property, value = match.Value } }),
        _ => throw new ConfigurationException("configuration_selector_invalid")
    };
}
