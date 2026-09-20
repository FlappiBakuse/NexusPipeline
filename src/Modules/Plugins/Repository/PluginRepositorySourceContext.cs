namespace NexusPipeline.Modules.Plugins.Repository;

/// <summary>一次仓库读取使用的不可变来源身份；通道切换只在 Host 重启时重建。</summary>
internal sealed record PluginRepositorySourceContext(string Channel, string CatalogUri)
{
    internal bool IsDevelop => string.Equals(Channel, "develop", StringComparison.Ordinal);

    internal static PluginRepositorySourceContext Stable { get; } = new(
        "stable",
        PluginRepositoryCatalog.CatalogUrl);

    internal static PluginRepositorySourceContext ForChannel(string channel)
    {
        return channel switch
        {
            "stable" => Stable,
            "develop" => new PluginRepositorySourceContext(
                "develop",
                "https://github.com/FlappiBakuse/NexusPipeline-Plugins/releases/download/plugins-develop/catalog.json"),
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "插件仓库通道无效"),
        };
    }
}
