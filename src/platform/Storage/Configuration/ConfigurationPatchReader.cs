using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class ConfigurationPatchReader(ContractReader contracts)
{
    public ConfigurationPatchRequest Read(ReadOnlyMemory<byte> bytes)
    {
        var wire = contracts.Read<ConfigPatch>(bytes, "config-patch.schema.json");
        var binding = new ConfigurationPatchBinding(Guid.Parse(wire.OperationId), wire.ResourceId, wire.ExpectedRevision, wire.SourceSha256);
        return ConfigurationPatchPolicy.Freeze(new ConfigurationPatchRequest(binding, wire.Edits.Select(edit => new ConfigurationValueEdit(
            ConfigurationSelectorProtocol.Read(edit.Selector, false), edit.Expected, edit.Value, edit.ExpectedExists ?? true)).ToImmutableArray()));
    }
}
