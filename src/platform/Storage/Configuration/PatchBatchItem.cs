using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed record PatchBatchItem(ConfigurationAddress Address, long ExpectedBindingRevision,
    ConfigurationContractStamp Contract, string RelativePath, ConfigurationSyntax Syntax,
    ConfigurationDocumentAccess Access, ReadOnlyMemory<byte> PatchBytes);
