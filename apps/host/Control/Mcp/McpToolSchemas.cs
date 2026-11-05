using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Host.Control.Mcp;

internal static class McpToolSchemas
{
    internal static JsonElement Empty { get; } = JsonSerializer.SerializeToElement(new
        { type = "object", properties = new { }, additionalProperties = false });
    internal static JsonElement Notification(string action)
    {
        using var input = typeof(ContractReader).Assembly.GetManifestResourceStream("Contracts.control-notification-command.schema.json")
            ?? throw new IOException("control_notification_contract_missing");
        var schema = JsonNode.Parse(input)?.AsObject() ?? throw new IOException("control_notification_contract_invalid");
        schema.Remove("$id");
        schema["properties"]!["action"] = new JsonObject { ["const"] = action };
        InlineUuid(schema, 0);
        return JsonSerializer.SerializeToElement(schema);
    }

    internal static JsonElement ExecutionSelection() => Execution("control-execution-selection.schema.json");
    internal static JsonElement EmulatorDiscovery() => Execution("control-emulator-discover.schema.json");
    internal static JsonElement ScriptSource()
    {
        var schema = JsonNode.Parse(Execution("control-script-source.schema.json").GetRawText())!;
        using var input = typeof(ContractReader).Assembly.GetManifestResourceStream("Contracts.common.schema.json")
            ?? throw new IOException("control_common_contract_missing");
        schema["properties"]!["extensionId"] = JsonNode.Parse(input)!["$defs"]!["extensionId"]!.DeepClone();
        return JsonSerializer.SerializeToElement(schema);
    }
    internal static JsonElement ConfigurationImport() => Execution("control-configuration-import.schema.json");
    internal static JsonElement Project(string action)
    {
        using var input = typeof(ContractReader).Assembly.GetManifestResourceStream("Contracts.control-project-command.schema.json")
            ?? throw new IOException("control_project_contract_missing");
        var schema = JsonNode.Parse(input)!.AsObject(); schema.Remove("$id");
        schema["properties"]!["action"] = new JsonObject { ["const"] = action };
        using var common = typeof(ContractReader).Assembly.GetManifestResourceStream("Contracts.common.schema.json")
            ?? throw new IOException("control_common_contract_missing");
        var definitions = JsonNode.Parse(common)!["$defs"]!.AsObject();
        foreach (var property in schema["properties"]!.AsObject().ToArray())
            if (property.Value is JsonObject value && value["$ref"]?.GetValue<string>() is { } reference &&
                reference.StartsWith("common.schema.json#/$defs/", StringComparison.Ordinal))
                schema["properties"]![property.Key] = definitions[reference["common.schema.json#/$defs/".Length..]]!.DeepClone();
        return JsonSerializer.SerializeToElement(schema);
    }
    internal static JsonElement ExecutionCancellation() => Execution("control-execution-cancellation.schema.json");
    private static JsonElement Execution(string name)
    {
        using var input = typeof(ContractReader).Assembly.GetManifestResourceStream("Contracts." + name)
            ?? throw new IOException("control_execution_contract_missing");
        var schema = JsonNode.Parse(input)?.AsObject() ?? throw new IOException("control_execution_contract_invalid");
        schema.Remove("$id"); InlineUuid(schema, 0);
        return JsonSerializer.SerializeToElement(schema);
    }

    private static void InlineUuid(JsonNode node, int depth)
    {
        if (depth > 32) throw new IOException("control_notification_contract_invalid");
        if (node is JsonObject value)
        {
            if (value["$ref"]?.GetValue<string>() == "common.schema.json#/$defs/uuid")
            { value.Remove("$ref"); value["type"] = "string"; value["format"] = "uuid"; }
            foreach (var child in value.Select(pair => pair.Value).OfType<JsonNode>().ToArray()) InlineUuid(child, depth + 1);
        }
        else if (node is JsonArray items)
            foreach (var child in items.OfType<JsonNode>()) InlineUuid(child, depth + 1);
    }
}
