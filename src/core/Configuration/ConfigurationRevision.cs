namespace NexusPipeline.Core.Configuration;

public sealed record ConfigurationAddress
{
    public Guid BindingId { get; }
    public string ResourceId { get; }
    public ConfigurationAddress(Guid bindingId, string resourceId)
    {
        if (bindingId == Guid.Empty || string.IsNullOrWhiteSpace(resourceId) || resourceId.Length > 120 ||
            resourceId.Any(char.IsControl))
            throw new ConfigurationException("configuration_address_invalid");
        BindingId = bindingId; ResourceId = resourceId;
    }
}

public sealed record ConfigurationContractStamp(long Revision, string DefinitionSha256, string? InstanceKey, bool RequiresInstance);
public enum ConfigurationUpdateKind { Manual, Progress, Repair }
public sealed record ConfigurationRevision(ConfigurationAddress Address, long Revision, Guid UserId, Guid ScriptId,
    ConfigurationContractStamp Contract, FileTreeSnapshot Tree);
public sealed record ConfigurationRevisionChange(ConfigurationAddress Address, long ExpectedRevision, long ExpectedBindingRevision,
    ConfigurationContractStamp Contract, FileTreeSnapshot Tree, ConfigurationUpdateKind Kind)
{
    public string? SourceRequestDigest { get; init; }
}
public sealed record ConfigurationSaveResult(Guid OperationId, ConfigurationRevision Saved, FileTreeDelta Delta, bool IsNew)
{
    public bool HasChanges { get; init; } = true;
}

public static class ConfigurationRevisionPolicy
{
    public static ConfigurationRevisionChange Freeze(ConfigurationRevisionChange change)
    {
        if (change is null || change.Address is null || change.ExpectedRevision is < 0 or >= 9007199254740991 ||
            change.ExpectedBindingRevision is < 1 or > 9007199254740991 || !Enum.IsDefined(change.Kind))
            throw new ConfigurationException("configuration_change_invalid");
        ValidateContract(change.Contract);
        if (change.SourceRequestDigest is not null && !FileTreePolicy.Digest(change.SourceRequestDigest))
            throw new ConfigurationException("configuration_source_request_invalid");
        return change with { Tree = FileTreePolicy.Freeze(change.Tree) };
    }

    public static ConfigurationRevision Next(ConfigurationRevision? before, ConfigurationRevisionChange change, Guid userId, Guid scriptId)
    {
        change = Freeze(change);
        if (userId == Guid.Empty || scriptId == Guid.Empty) throw new ConfigurationException("configuration_owner_invalid");
        if ((before?.Revision ?? 0) != change.ExpectedRevision || before is not null &&
            (before.Address != change.Address || before.UserId != userId || before.ScriptId != scriptId))
            throw new ConfigurationException("configuration_revision_conflict");
        if (change.Kind != ConfigurationUpdateKind.Manual && (before is null || before.Contract != change.Contract))
            throw new ConfigurationException("configuration_contract_changed");
        return new(change.Address, checked(change.ExpectedRevision + 1), userId, scriptId, change.Contract, change.Tree);
    }

    public static void RequireCurrentContract(ConfigurationRevision? saved, ConfigurationContractStamp expected)
    {
        ValidateContract(expected);
        if (saved is null || saved.Contract != expected) throw new ConfigurationException("configuration_required");
    }

    public static void ValidateContract(ConfigurationContractStamp contract)
    {
        if (contract is null || contract.Revision is < 1 or > 9007199254740991 || !FileTreePolicy.Digest(contract.DefinitionSha256) ||
            contract.InstanceKey is { } key && (key.Length is < 1 or > 256 || key.Any(char.IsControl)))
            throw new ConfigurationException("configuration_contract_invalid");
        if (contract.RequiresInstance && contract.InstanceKey is null)
            throw new ConfigurationException("configuration_instance_selection_required");
    }
}
