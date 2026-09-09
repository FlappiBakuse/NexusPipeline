using System.Text.Json.Nodes;
using NexusPipeline.Utilities;

namespace NexusPipeline.Plugins;

internal sealed record PluginAuthor(string Name, string Url);

internal sealed record PluginLocalizedMetadata(
    string DisplayName,
    string GameName,
    string Description,
    IReadOnlyList<string> Tags,
    IReadOnlyList<PluginChangelogEntry> Changelog);

internal sealed record PluginPresentationMetadata(
    string GameName,
    IReadOnlyList<PluginAuthor> Authors,
    IReadOnlyList<string> Tags,
    string Homepage,
    string CreatedAt,
    string UpdatedAt,
    IReadOnlyList<PluginChangelogEntry> Changelog,
    bool HasReadme,
    IReadOnlyDictionary<string, PluginLocalizedMetadata> Locales)
{
    public static PluginPresentationMetadata Empty(string gameName, bool hasReadme = false)
    {
        return new PluginPresentationMetadata(
            gameName?.Trim() ?? "",
            Array.Empty<PluginAuthor>(),
            Array.Empty<string>(),
            "",
            "",
            "",
            Array.Empty<PluginChangelogEntry>(),
            hasReadme,
            new Dictionary<string, PluginLocalizedMetadata>(StringComparer.OrdinalIgnoreCase));
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
            if (!TryParseLocales(
                    root,
                    Path.GetFileName(pluginDirectory),
                    version,
                    changelog,
                    out IReadOnlyDictionary<string, PluginLocalizedMetadata> locales,
                    out string? localesError))
            {
                throw new InvalidDataException(localesError ?? "locales 无效");
            }
            string createdAt = root["createdAt"]?.ToString()?.Trim() ?? "";
            if (createdAt.Length > 0 && !PluginRepositoryCatalog.TryParseDate(createdAt))
            {
                throw new InvalidDataException("createdAt 必须使用 YYYY-MM-DD 格式");
            }
            string updatedAt = changelog.FirstOrDefault()?.Date ?? "";
            if (createdAt.Length > 0
                && updatedAt.Length > 0
                && string.CompareOrdinal(createdAt, updatedAt) > 0)
            {
                throw new InvalidDataException("createdAt 不能晚于更新时间");
            }
            return new PluginPresentationMetadata(
                gameName,
                authors,
                tags,
                homepage,
                createdAt,
                updatedAt,
                changelog,
                hasReadme,
                locales);
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

    public static bool TryParseLocales(
        JsonObject root,
        string pluginName,
        string version,
        IReadOnlyList<PluginChangelogEntry> baseChangelog,
        out IReadOnlyDictionary<string, PluginLocalizedMetadata> locales,
        out string? error)
    {
        locales = new Dictionary<string, PluginLocalizedMetadata>(StringComparer.OrdinalIgnoreCase);
        error = null;
        JsonNode? node = root["locales"];
        if (node is null)
        {
            return true;
        }
        if (node is not JsonObject localeRoot)
        {
            error = "locales 必须是对象";
            return false;
        }
        var parsed = new Dictionary<string, PluginLocalizedMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach ((string rawLocale, JsonNode? localeNode) in localeRoot)
        {
            string locale = rawLocale.Trim().ToLowerInvariant() switch
            {
                "zh" or "zh-cn" => "zh-CN",
                "en" or "en-us" => "en-US",
                _ => "",
            };
            if (locale.Length == 0 || localeNode is not JsonObject localeObject || parsed.ContainsKey(locale))
            {
                error = $"locales locale 无效或重复：{rawLocale}";
                return false;
            }
            string displayName = localeObject["displayName"]?.ToString()?.Trim() ?? "";
            string gameName = localeObject["gameName"]?.ToString()?.Trim() ?? "";
            string description = localeObject["description"]?.ToString()?.Trim() ?? "";
            if (!ValidText(displayName, 128) || !ValidText(gameName, 128) || !ValidText(description, 2048))
            {
                error = $"locales.{locale} 的展示字段无效";
                return false;
            }
            if (!TryParseTags(localeObject["tags"], out IReadOnlyList<string> tags))
            {
                error = $"locales.{locale}.tags 无效";
                return false;
            }
            if (localeObject["changelog"] is not JsonArray changelogNodes
                || changelogNodes.Count != baseChangelog.Count)
            {
                error = $"locales.{locale}.changelog 版本数量与基础记录不一致";
                return false;
            }
            var changelog = new List<PluginChangelogEntry>(changelogNodes.Count);
            for (int index = 0; index < changelogNodes.Count; index++)
            {
                if (changelogNodes[index] is not JsonObject changeObject)
                {
                    error = $"locales.{locale}.changelog 条目无效";
                    return false;
                }
                string changeVersion = changeObject["version"]?.ToString()?.Trim() ?? "";
                if (!string.Equals(changeVersion, baseChangelog[index].Version, StringComparison.Ordinal))
                {
                    error = $"locales.{locale}.changelog 版本必须与基础记录一致";
                    return false;
                }
                if (changeObject["items"] is not JsonArray itemNodes || itemNodes.Count is < 1 or > 32)
                {
                    error = $"locales.{locale}.changelog items 数量无效";
                    return false;
                }
                var items = new List<string>(itemNodes.Count);
                foreach (JsonNode? itemNode in itemNodes)
                {
                    string item = itemNode?.ToString()?.Trim() ?? "";
                    if (!ValidText(item, 512))
                    {
                        error = $"locales.{locale}.changelog 文本无效";
                        return false;
                    }
                    items.Add(item);
                }
                changelog.Add(new PluginChangelogEntry(changeVersion, baseChangelog[index].Date, items));
            }
            parsed[locale] = new PluginLocalizedMetadata(displayName, gameName, description, tags, changelog);
        }
        locales = parsed;
        return true;
    }

    private static bool TryParseTags(
        JsonNode? node,
        out IReadOnlyList<string> tags)
    {
        tags = Array.Empty<string>();
        if (node is not JsonArray tagNodes || tagNodes.Count > MaxTags)
        {
            return false;
        }
        var parsed = new List<string>(tagNodes.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? tagNode in tagNodes)
        {
            string tag = tagNode?.ToString()?.Trim() ?? "";
            if (!ValidText(tag, MaxTagLength) || !seen.Add(tag))
            {
                return false;
            }
            parsed.Add(tag);
        }
        tags = parsed;
        return true;
    }

    private static bool ValidText(string value, int maximum)
    {
        return value.Length > 0
            && value.Length <= maximum
            && !value.Contains('<')
            && !value.Contains('>');
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
