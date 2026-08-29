using System.Text.Json.Nodes;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

internal sealed record PluginAuthor(string Name, string Url);

internal sealed record PluginPresentationMetadata(
    string GameName,
    IReadOnlyList<PluginAuthor> Authors,
    IReadOnlyList<string> Tags,
    string Homepage,
    string UpdatedAt,
    IReadOnlyList<PluginChangelogEntry> Changelog,
    bool HasReadme)
{
    public static PluginPresentationMetadata Empty(string gameName, bool hasReadme = false)
    {
        return new PluginPresentationMetadata(
            gameName?.Trim() ?? "",
            Array.Empty<PluginAuthor>(),
            Array.Empty<string>(),
            "",
            "",
            Array.Empty<PluginChangelogEntry>(),
            hasReadme);
    }
}

internal static class PluginPresentationMetadataParser
{
    private const int MaxAuthors = 8;
    private const int MaxAuthorNameLength = 64;
    private const int MaxUrlLength = 2048;
    private const int MaxTags = 16;
    private const int MaxTagLength = 32;

    public static PluginPresentationMetadata LoadLocal(
        string pluginDirectory,
        string fallbackGameName,
        string version)
    {
        bool hasReadme = File.Exists(Path.Combine(pluginDirectory, "README.md"));
        string path = Path.Combine(pluginDirectory, "store.json");
        if (!File.Exists(path))
        {
            return PluginPresentationMetadata.Empty(fallbackGameName, hasReadme);
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            {
                throw new InvalidDataException("store.json 不是 JSON 对象");
            }
            if (root["schemaVersion"]?.GetValue<int>() != 1)
            {
                throw new InvalidDataException("store.json schemaVersion 不受支持");
            }
            string gameName = root["gameName"]?.ToString()?.Trim() ?? fallbackGameName.Trim();
            if (gameName.Length > 128)
            {
                throw new InvalidDataException("gameName 过长");
            }
            if (!TryParseFields(root, out IReadOnlyList<PluginAuthor> authors, out IReadOnlyList<string> tags, out string homepage, out _, out string? fieldError))
            {
                throw new InvalidDataException(fieldError ?? "展示元数据无效");
            }
            if (!PluginRepositoryCatalog.TryParseChangelog(
                    root,
                    version,
                    required: false,
                    out IReadOnlyList<PluginChangelogEntry> changelog,
                    out string? changelogError))
            {
                throw new InvalidDataException(changelogError ?? "changelog 无效");
            }
            return new PluginPresentationMetadata(
                gameName,
                authors,
                tags,
                homepage,
                changelog.FirstOrDefault()?.Date ?? "",
                changelog,
                hasReadme);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[插件] 读取展示元数据失败：{Path.GetFileName(pluginDirectory)}（{ex.Message}）");
            return PluginPresentationMetadata.Empty(fallbackGameName, hasReadme);
        }
    }

    public static bool TryParseFields(
        JsonObject root,
        out IReadOnlyList<PluginAuthor> authors,
        out IReadOnlyList<string> tags,
        out string homepage,
        out bool hasReadme,
        out string? error)
    {
        authors = Array.Empty<PluginAuthor>();
        tags = Array.Empty<string>();
        homepage = "";
        hasReadme = false;
        error = null;
        try
        {
            if (root["authors"] is JsonNode authorsNode)
            {
                if (authorsNode is not JsonArray authorNodes || authorNodes.Count > MaxAuthors)
                {
                    error = $"authors 必须是最多包含 {MaxAuthors} 个对象的数组";
                    return false;
                }
                var parsedAuthors = new List<PluginAuthor>(authorNodes.Count);
                foreach (JsonNode? authorNode in authorNodes)
                {
                    if (authorNode is not JsonObject author)
                    {
                        error = "authors 中的条目必须是对象";
                        return false;
                    }
                    string name = author["name"]?.ToString()?.Trim() ?? "";
                    if (name.Length is < 1 or > MaxAuthorNameLength || name.Contains('<') || name.Contains('>'))
                    {
                        error = "作者名称长度或内容无效";
                        return false;
                    }
                    string url = author["url"]?.ToString()?.Trim() ?? "";
                    if (!TryValidateHttpsUrl(url, allowEmpty: true))
                    {
                        error = "作者链接必须是 HTTPS 地址";
                        return false;
                    }
                    parsedAuthors.Add(new PluginAuthor(name, url));
                }
                authors = parsedAuthors;
            }

            if (root["tags"] is JsonNode tagsNode)
            {
                if (tagsNode is not JsonArray tagNodes || tagNodes.Count > MaxTags)
                {
                    error = $"tags 必须是最多包含 {MaxTags} 个文本的数组";
                    return false;
                }
                var parsedTags = new List<string>(tagNodes.Count);
                var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonNode? tagNode in tagNodes)
                {
                    string tag = tagNode?.ToString()?.Trim() ?? "";
                    if (tag.Length is < 1 or > MaxTagLength || tag.Contains('<') || tag.Contains('>') || !seenTags.Add(tag))
                    {
                        error = "标签长度、内容或重复项无效";
                        return false;
                    }
                    parsedTags.Add(tag);
                }
                tags = parsedTags;
            }

            homepage = root["homepage"]?.ToString()?.Trim() ?? "";
            if (!TryValidateHttpsUrl(homepage, allowEmpty: true))
            {
                error = "homepage 必须是 HTTPS 地址";
                return false;
            }

            if (root["hasReadme"] is JsonNode readmeNode)
            {
                hasReadme = readmeNode.GetValue<bool>();
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryValidateHttpsUrl(string value, bool allowEmpty)
    {
        if (value.Length == 0)
        {
            return allowEmpty;
        }
        return value.Length <= MaxUrlLength
            && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrWhiteSpace(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Fragment);
    }
}
