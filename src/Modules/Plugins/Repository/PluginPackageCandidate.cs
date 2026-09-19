namespace NexusPipeline.Modules.Plugins.Repository;

/// <summary>从严格解析的 catalog entry 冻结的包身份；批量更新不得按名称重新读取候选。</summary>
internal sealed record PluginPackageCandidate(
    string Name,
    string ArtifactName,
    string Version,
    string Sha256,
    long SizeBytes,
    string PackageUrl,
    string Channel,
    string SourceCommit)
{
    internal PluginCatalogEntry FrozenEntry { get; init; } = null!;

    internal static PluginPackageCandidate FromEntry(
        PluginCatalogEntry entry,
        PluginRepositorySourceContext source)
    {
        if (!string.Equals(entry.Channel, source.Channel, StringComparison.Ordinal))
        {
            throw new InvalidDataException("插件候选与当前仓库通道不一致");
        }

        return new PluginPackageCandidate(
            entry.Name,
            entry.ArtifactName,
            entry.Version,
            entry.Sha256,
            entry.SizeBytes,
            entry.PackageUrl,
            entry.Channel,
            entry.SourceCommit)
        {
            FrozenEntry = FreezeEntry(entry),
        };
    }

    private static PluginCatalogEntry FreezeEntry(PluginCatalogEntry entry)
    {
        return entry with
        {
            Capabilities = entry.Capabilities.ToArray(),
            Changelog = entry.Changelog
                .Select(item => item with { Items = item.Items.ToArray() })
                .ToArray(),
            Authors = entry.Authors.ToArray(),
            Tags = entry.Tags.ToArray(),
            Locales = entry.Locales.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase),
        };
    }
}
