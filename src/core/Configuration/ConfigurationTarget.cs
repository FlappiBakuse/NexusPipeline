using System.Collections.Immutable;

namespace NexusPipeline.Core.Configuration;

public sealed record ConfigurationTarget(ConfigurationAddress Address, long ExpectedRevision,
    ConfigurationContractStamp Contract, string TargetPath)
{
    public EditWritePolicy? WritePolicy { get; init; }
}

public static class ConfigurationTargetPolicy
{
    public static ImmutableArray<ConfigurationTarget> Freeze(ImmutableArray<ConfigurationTarget> targets)
    {
        if (targets.IsDefaultOrEmpty || targets.Length > 32 || targets.Any(target => target is null || target.Address is null))
            throw new ConfigurationException("configuration_edit_request_invalid");
        targets = targets.OrderBy(target => target.Address.ResourceId, StringComparer.Ordinal).ToImmutableArray();
        if (targets.Select(target => target.Address.BindingId).Distinct().Count() != 1 || targets.Select(target => target.Address).Distinct().Count() != targets.Length)
            throw new ConfigurationException("configuration_edit_owner_invalid");
        foreach (var target in targets)
        {
            ConfigurationRevisionPolicy.ValidateContract(target.Contract);
            if (target.ExpectedRevision is < 0 or >= 9007199254740991 || string.IsNullOrWhiteSpace(target.TargetPath) ||
                target.TargetPath.Length > 32767 || target.TargetPath.Any(char.IsControl))
                throw new ConfigurationException("configuration_edit_target_invalid");
        }
        return targets.Select(target => target with { WritePolicy = target.WritePolicy is null ? null : EditWriteRules.Freeze(target.WritePolicy) }).ToImmutableArray();
    }
}
