using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Configuration;

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
