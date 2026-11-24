using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using System.Collections.Immutable;
using System.Text.Json;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class ConfigurationPatchReader(ContractReader contracts)
{
    public ConfigurationPatchRequest Read(ReadOnlyMemory<byte> bytes) => Decode(contracts.Read<ConfigPatch>(bytes, "config-patch.schema.json"));

    internal static ConfigurationPatchRequest DecodeValidated(JsonElement value) =>
        Decode(value.Deserialize<ConfigPatch>() ?? throw new JsonException("null_contract"));

    private static ConfigurationPatchRequest Decode(ConfigPatch wire)
    {
        var binding = new ConfigurationPatchBinding(Guid.Parse(wire.OperationId), wire.ResourceId, wire.ExpectedRevision, wire.SourceSha256);
        return ConfigurationPatchPolicy.Freeze(new ConfigurationPatchRequest(binding, wire.Edits.Select(edit => new ConfigurationValueEdit(
            ConfigurationSelectorProtocol.Read(edit.Selector, false), edit.Expected, edit.Value, edit.ExpectedExists ?? true)).ToImmutableArray()));
    }
}

public sealed class ConfigurationPatchWriter(ContractReader contracts)
{
    public byte[] Write(ConfigurationPatchRequest request)
    {
        request = ConfigurationPatchPolicy.Freeze(request);
        var wire = new ConfigPatch
        {
            Format = "nexus.config.patch", Version = 1, OperationId = request.Binding.OperationId.ToString("D"),
            ResourceId = request.Binding.ResourceId, ExpectedRevision = request.Binding.Revision, SourceSha256 = request.Binding.SourceSha256,
            Edits = request.Edits.Select(edit => new ConfigPatchEditsItem
            {
                Selector = ConfigurationSelectorProtocol.Write(edit.Selector, false), Expected = edit.Expected, Value = edit.Value,
                ExpectedExists = edit.ExpectedExists
            }).ToArray()
        };
        var bytes = BoundedJson.Serialize(wire);
        contracts.Read<ConfigPatch>(bytes, "config-patch.schema.json");
        return bytes;
    }
}
