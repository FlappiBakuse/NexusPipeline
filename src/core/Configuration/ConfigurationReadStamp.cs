using System.Collections.Immutable;

namespace NexusPipeline.Core.Configuration;

public sealed record ConfigurationReadStamp(ConfigurationAddress Address, long ExpectedRevision,
    long ExpectedBindingRevision, ConfigurationContractStamp Contract, string RelativePath, string SourceSha256);

public static class ConfigurationReadPolicy
{
    public static ImmutableArray<ConfigurationReadStamp> Freeze(IReadOnlyList<ConfigurationReadStamp>? source)
    {
        if (source is null) return [];
        if (source.Count > 128) throw new ConfigurationException("configuration_read_set_limit");
        var result = source.Select(item =>
        {
            if (item is null || item.Address is null || item.Address.BindingId == Guid.Empty ||
                string.IsNullOrWhiteSpace(item.Address.ResourceId) || item.Address.ResourceId.Length > 120 ||
                item.Address.ResourceId.Any(char.IsControl) || item.ExpectedRevision is < 1 or > 9007199254740991 ||
                item.ExpectedBindingRevision is < 1 or > 9007199254740991 ||
                !FileTreePolicy.RelativePath(item.RelativePath) || !FileTreePolicy.Digest(item.SourceSha256))
                throw new ConfigurationException("configuration_read_set_invalid");
            ConfigurationRevisionPolicy.ValidateContract(item.Contract);
            return item with { };
        }).ToImmutableArray();
        if (result.Select(item => (item.Address, item.RelativePath.ToUpperInvariant())).Distinct().Count() != result.Length ||
            result.Select(item => item.Address.BindingId).Distinct().Count() > 1 ||
            result.Select(item => item.ExpectedBindingRevision).Distinct().Count() > 1 ||
            result.GroupBy(item => item.Address).Any(group => group.Select(item => (item.ExpectedRevision, item.Contract)).Distinct().Count() != 1))
            throw new ConfigurationException("configuration_read_set_invalid");
        return result;
    }
}
