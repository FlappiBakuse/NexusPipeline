using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Platform.Extensions;

internal static class TaskResourceValue
{
    internal static TaskDefinitionsResource Create(Guid operationId, string resourceId, long revision,
        string digest, ReadOnlyMemory<byte> bytes, ConfigurationSyntax syntax)
    {
        var content = new UTF8Encoding(false, true).GetString(bytes.Span);
        if (content.Length > 262144) throw new IOException("adapter_resource_input_limit");
        return new()
        {
            ResourceId = resourceId, Revision = revision, Sha256 = digest, Content = content, Syntax = Wire(syntax),
            Value = syntax == ConfigurationSyntax.Opaque ? null : Parse(operationId, resourceId, revision, digest, bytes, syntax)
        };
    }

    internal static void Verify(TaskDefinitionsResource resource, Guid operationId)
    {
        if (resource.RelativePath is { Length: > 0 } relative)
        {
            try { Storage.PhysicalPathPolicy.ValidateRelativePath(relative); }
            catch (Exception error) when (error is IOException or ArgumentException)
            { throw new JsonException("task_resource_path_invalid", error); }
        }
        if (resource.Syntax is null)
        {
            if (resource.Value is not null) throw new JsonException("task_resource_syntax_required");
            return;
        }
        var syntax = resource.Syntax switch
        {
            "opaque" => ConfigurationSyntax.Opaque, "json" => ConfigurationSyntax.Json,
            "jsonWithComments" => ConfigurationSyntax.JsonWithComments, "yaml" => ConfigurationSyntax.Yaml,
            _ => throw new JsonException("task_resource_syntax_invalid")
        };
        if (syntax == ConfigurationSyntax.Opaque)
        {
            if (resource.Value is not null) throw new JsonException("task_resource_opaque_value_invalid");
            return;
        }
        if (resource.Value is not { } value) throw new JsonException("task_resource_value_required");
        var original = Parse(operationId, resource.ResourceId, resource.Revision ?? 1, resource.Sha256,
            Encoding.UTF8.GetBytes(resource.Content), syntax);
        if (!JsonElement.DeepEquals(original, value)) throw new JsonException("task_resource_value_changed");
    }

    private static JsonElement Parse(Guid operationId, string resourceId, long revision, string digest,
        ReadOnlyMemory<byte> bytes, ConfigurationSyntax syntax)
    {
        var root = new ConfigurationSelector([]);
        // This converts bytes already supplied to the adapter; patch authority remains with the original access declaration.
        using var document = new ConfigurationDocument(new(operationId, resourceId, revision, digest), syntax,
            new([new(root, true)], [], []), bytes);
        return document.Read(root);
    }

    private static string Wire(ConfigurationSyntax syntax) => syntax switch
    {
        ConfigurationSyntax.Opaque => "opaque", ConfigurationSyntax.Json => "json",
        ConfigurationSyntax.JsonWithComments => "jsonWithComments", ConfigurationSyntax.Yaml => "yaml",
        _ => throw new ArgumentException("task_resource_syntax_invalid")
    };
}
