namespace NexusPipeline.Plugins;

/// <summary>将官方插件身份映射到 NexusPipeline-Plugins 的源码目录和资源地址。</summary>
internal static class OfficialPluginSourcePaths
{
    private const string RawRepositoryPrefix =
        "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/";

    internal static bool TryGetSourceDirectory(
        string kind,
        string artifactName,
        out string? relativeDirectory,
        out string? error)
    {
        relativeDirectory = null;
        error = null;
        if (!PluginRepositoryCatalog.IsSafeArtifactName(artifactName))
        {
            error = "插件 artifactName 无效";
            return false;
        }

        string? category = kind.Trim().ToLowerInvariant() switch
        {
            "managed-code" => "general",
            "data-specialized" => "specialized",
            _ => null,
        };
        if (category is null)
        {
            error = $"不支持的插件类型：{kind}";
            return false;
        }

        relativeDirectory = $"plugins/{category}/{artifactName}";
        return true;
    }

    internal static bool TryGetReadmeUri(
        string kind,
        string artifactName,
        out Uri? uri,
        out string? error)
    {
        uri = null;
        if (!TryGetSourceDirectory(kind, artifactName, out string? directory, out error)
            || directory is null)
        {
            return false;
        }

        string source = RawRepositoryPrefix
            + directory
            + "/README.md";
        if (!Uri.TryCreate(source, UriKind.Absolute, out uri))
        {
            error = "官方插件 README 地址无效";
            uri = null;
            return false;
        }
        return true;
    }
}
