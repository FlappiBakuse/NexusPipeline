namespace NexusPipeline.Modules.Plugins;

/// <summary>插件运行时名称的纯 canonicalization；跨实体迁移由 Host 启动适配器负责。</summary>
internal static class PluginNameCanonicalization
{
    internal const string LegacyMaaStellaSora = "maastellasora";
    internal const string MaaStellaSora = "maas";

    internal static string Canonicalize(string? name)
    {
        string value = name?.Trim() ?? "";
        return value.Equals(LegacyMaaStellaSora, StringComparison.OrdinalIgnoreCase)
            ? MaaStellaSora
            : value;
    }

    internal static string? LegacyNameFor(string canonicalName)
    {
        return canonicalName.Equals(MaaStellaSora, StringComparison.OrdinalIgnoreCase)
            ? LegacyMaaStellaSora
            : null;
    }
}
