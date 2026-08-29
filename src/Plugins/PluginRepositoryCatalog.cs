using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Plugins;

/// <summary>官方插件仓库 catalog.json 的严格解析与供应链字段校验。</summary>
internal static class PluginRepositoryCatalog
{
    public const int SchemaVersion = 2;
    public const int LegacySchemaVersion = 1;
    public const string Repository = "FlappiBakuse/NexusPipeline-Plugins";
    public const string CatalogUrl = "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/catalog.json";
    public const string PackageUrlPrefix = "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/packages/";
    public const string LegacyPackageUrlPrefix = "https://github.com/FlappiBakuse/NexusPipeline-Plugins/releases/download/";
    public const long MaxCatalogBytes = 8L * 1024 * 1024;
    public const long MaxPackageBytes = 200L * 1024 * 1024;
    public const int MaxEntries = 4096;

    public static bool TryParse(string json, out PluginCatalog? catalog, out string? error)
    {
        catalog = null;
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "catalog.json 为空";
                return false;
            }
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                error = "catalog.json 不是 JSON 对象";
                return false;
            }
            int schemaVersion = RequiredInt(root, "schemaVersion");
            if (schemaVersion is not (LegacySchemaVersion or SchemaVersion))
            {
                error = $"不支持的插件 catalog schemaVersion：{schemaVersion}";
                return false;
            }
            string repository = RequiredString(root, "repository");
            if (!string.Equals(repository, Repository, StringComparison.Ordinal))
            {
                error = $"插件仓库标识不受信任：{repository}";
                return false;
            }
            string generatedAt = RequiredString(root, "generatedAt");
            if (!DateTimeOffset.TryParse(generatedAt, out _))
            {
                error = "catalog.json 的 generatedAt 无效";
                return false;
            }
            if (root["plugins"] is not JsonArray pluginNodes)
            {
                error = "catalog.json 缺少 plugins 数组";
                return false;
            }
            if (pluginNodes.Count > MaxEntries)
            {
                error = $"插件条目数量超过上限（{MaxEntries}）";
                return false;
            }

            var entries = new List<PluginCatalogEntry>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var artifactNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var replacedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonNode? node in pluginNodes)
            {
                if (node is not JsonObject item)
                {
                    error = "插件 catalog 条目必须是 JSON 对象";
                    return false;
                }
                string name = RequiredString(item, "name");
                bool validName = schemaVersion == SchemaVersion
                    ? IsCanonicalPluginId(name)
                    : IsSafePluginName(name);
                if (!validName || !names.Add(name))
                {
                    error = $"插件 name 无效或重复：{name}";
                    return false;
                }
                string displayName = RequiredString(item, "displayName");
                string gameName = item["gameName"]?.ToString()?.Trim() ?? "";
                string description = item["description"]?.ToString()?.Trim() ?? "";
                if (displayName.Length > 128 || gameName.Length > 128 || description.Length > 2048)
                {
                    error = $"插件 {name} 的展示字段过长";
                    return false;
                }
                string version = RequiredString(item, "version");
                if (!TryParseVersion(version, out _))
                {
                    error = $"插件 {name} 的 version 无效：{version}";
                    return false;
                }
                string artifactName = item["artifactName"]?.ToString()?.Trim() ?? "";
                if (schemaVersion == SchemaVersion)
                {
                    if (!IsSafeArtifactName(artifactName) || !artifactNames.Add(artifactName))
                    {
                        error = $"插件 {name} 的 artifactName 无效或重复：{artifactName}";
                        return false;
                    }
                }
                else if (!string.IsNullOrEmpty(artifactName) && !IsSafeArtifactName(artifactName))
                {
                    error = $"插件 {name} 的 artifactName 无效：{artifactName}";
                    return false;
                }
                string kind = (item["kind"]?.ToString()?.Trim().ToLowerInvariant()) ?? "";
                if (kind == "specialized")
                {
                    kind = "data-specialized";
                }
                if (kind is not ("data-specialized" or "managed-code"))
                {
                    error = $"插件 {name} 的 kind 不受支持：{kind}";
                    return false;
                }
                string apiVersion = item["apiVersion"]?.ToString()?.Trim() ?? "";
                if (kind == "managed-code" && !TryParseApiVersion(apiVersion, out _, out _))
                {
                    error = $"managed-code 插件 {name} 的 apiVersion 无效：{apiVersion}";
                    return false;
                }
                var capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (item["capabilities"] is JsonArray capabilityNodes)
                {
                    if (capabilityNodes.Count > 64)
                    {
                        error = $"插件 {name} 的 capabilities 数量过多";
                        return false;
                    }
                    foreach (JsonNode? capabilityNode in capabilityNodes)
                    {
                        string capability = capabilityNode?.ToString()?.Trim() ?? "";
                        if (!string.IsNullOrWhiteSpace(capability))
                        {
                            if (capability.Length > 64)
                            {
                                error = $"插件 {name} 的 capability 过长";
                                return false;
                            }
                            capabilities.Add(capability);
                        }
                    }
                }
                string minHostVersion = item["minHostVersion"]?.ToString()?.Trim() ?? "0.0.0";
                if (!TryParseVersion(minHostVersion, out _))
                {
                    error = $"插件 {name} 的 minHostVersion 无效：{minHostVersion}";
                    return false;
                }
                string packageUrl = RequiredString(item, "packageUrl");
                string? packageUrlError = ValidatePackageUrl(
                    packageUrl,
                    allowLegacyRelease: schemaVersion == LegacySchemaVersion,
                    artifactName,
                    version);
                if (packageUrlError is not null)
                {
                    error = $"插件 {name} 的 packageUrl 无效：{packageUrlError}";
                    return false;
                }
                string sha256 = RequiredString(item, "sha256").ToLowerInvariant();
                if (sha256.Length != 64 || sha256.Any(ch => !Uri.IsHexDigit(ch)))
                {
                    error = $"插件 {name} 的 sha256 无效";
                    return false;
                }
                long sizeBytes = RequiredLong(item, "sizeBytes");
                if (sizeBytes is <= 0 or > MaxPackageBytes)
                {
                    error = $"插件 {name} 的 sizeBytes 超出范围";
                    return false;
                }
                if (!TryParseReplaces(
                        item,
                        name,
                        out IReadOnlyList<string> replaces,
                        out string? replacesError,
                        requireCanonicalNames: schemaVersion == SchemaVersion))
                {
                    error = $"插件 {name} 的 replaces 无效：{replacesError}";
                    return false;
                }
                foreach (string replaced in replaces)
                {
                    if (!replacedNames.Add(replaced))
                    {
                        error = $"插件 replacement 来源重复：{replaced}";
                        return false;
                    }
                }

                if (!TryParseChangelog(item, version, schemaVersion == SchemaVersion, out IReadOnlyList<PluginChangelogEntry> changelog, out string? changelogError))
                {
                    error = $"插件 {name} 的 changelog 无效：{changelogError}";
                    return false;
                }

                entries.Add(new PluginCatalogEntry(
                    name,
                    displayName,
                    gameName,
                    description,
                    version,
                    kind,
                    apiVersion,
                    capabilities.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                    minHostVersion,
                    packageUrl,
                    sha256,
                    sizeBytes,
                    replaces)
                {
                    ArtifactName = artifactName,
                    CatalogSchemaVersion = schemaVersion,
                    Changelog = changelog,
                });
            }
            catalog = new PluginCatalog(schemaVersion, repository, generatedAt, entries);
            return true;
        }
        catch (Exception ex)
        {
            error = $"catalog.json 解析失败：{ex.Message}";
            return false;
        }
    }

    public static string? ValidatePackageUrl(string value)
    {
        return ValidatePackageUrl(value, allowLegacyRelease: false, artifactName: null, version: null);
    }

    public static string? ValidatePackageUrl(
        string value,
        bool allowLegacyRelease,
        string? artifactName,
        string? version)
    {
        string candidate = value?.Trim() ?? "";
        if (candidate.Contains('%', StringComparison.Ordinal)
            || !Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
        {
            return "地址格式无效";
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return "地址必须是无附加参数的官方 HTTPS 插件包地址";
        }

        const string rawPrefix = "/FlappiBakuse/NexusPipeline-Plugins/main/packages/";
        const string legacyPrefix = "/FlappiBakuse/NexusPipeline-Plugins/releases/download/";
        bool raw = string.Equals(uri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith(rawPrefix, StringComparison.Ordinal);
        bool legacy = allowLegacyRelease
            && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith(legacyPrefix, StringComparison.Ordinal);
        if (!raw && !legacy)
        {
            return allowLegacyRelease
                ? "地址必须指向官方插件仓库的 raw 文件或旧版 GitHub Release 资产"
                : "地址必须指向官方插件仓库 main/packages 下的 raw 文件";
        }

        string prefix = raw ? rawPrefix : legacyPrefix;
        string packagePath = uri.AbsolutePath[prefix.Length..];
        string[] segments = packagePath.Split('/', StringSplitOptions.None);
        if (segments.Length != 2
            || !IsSafeUrlSegment(segments[0])
            || !IsSafeUrlSegment(segments[1])
            || !segments[1].EndsWith(".zip", StringComparison.Ordinal))
        {
            return "地址缺少有效的插件 ZIP 资产名";
        }
        if (raw && !IsSafeArtifactName(segments[0]))
        {
            return "raw 插件包目录名不符合大小写命名规范";
        }
        if (raw)
        {
            string artifactPrefix = $"{segments[0]}-";
            if (!segments[1].StartsWith(artifactPrefix, StringComparison.Ordinal)
                || !TryParseVersion(segments[1][artifactPrefix.Length..^4], out _))
            {
                return "raw 插件包文件名必须匹配 artifactName-版本.zip";
            }
        }
        if (artifactName is not null && version is not null && raw
            && (!string.Equals(segments[0], artifactName, StringComparison.Ordinal)
                || !string.Equals(segments[1], $"{artifactName}-{version}.zip", StringComparison.Ordinal)))
        {
            return "插件包地址与 artifactName、version 不一致";
        }
        return null;
    }

    public static bool IsSafeArtifactName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || !char.IsAsciiLetter(value[0]))
        {
            return false;
        }
        bool hasUppercase = false;
        foreach (char ch in value)
        {
            if (!char.IsAsciiLetterOrDigit(ch))
            {
                return false;
            }
            hasUppercase |= char.IsAsciiLetter(ch) && char.IsUpper(ch);
        }
        return hasUppercase;
    }

    private static bool IsSafeUrlSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..")
        {
            return false;
        }
        foreach (char ch in value)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryParseChangelog(
        JsonObject item,
        string currentVersion,
        bool required,
        out IReadOnlyList<PluginChangelogEntry> changelog,
        out string? error)
    {
        changelog = Array.Empty<PluginChangelogEntry>();
        error = null;
        JsonNode? node = item["changelog"];
        if (node is null)
        {
            if (required)
            {
                error = "必须是包含 1 至 3 个版本的数组";
                return false;
            }
            return true;
        }
        if (node is not JsonArray array || array.Count is < 1 or > 3)
        {
            error = "必须是包含 1 至 3 个版本的数组";
            return false;
        }

        var parsed = new List<PluginChangelogEntry>(array.Count);
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonObject entry)
            {
                error = "每条记录必须是 JSON 对象";
                return false;
            }
            string version = RequiredString(entry, "version");
            if (!TryParseVersion(version, out _)
                || !versions.Add(version)
                || index == 0 && !string.Equals(version, currentVersion, StringComparison.Ordinal))
            {
                error = index == 0 ? "第一条记录必须对应当前插件版本" : "版本号无效、重复或未按从新到旧排列";
                return false;
            }
            if (index > 0 && CompareVersions(parsed[index - 1].Version, version) <= 0)
            {
                error = "版本记录必须按从新到旧排列";
                return false;
            }
            string date = RequiredString(entry, "date");
            if (date.Length != 10
                || !DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                error = "日期必须使用 YYYY-MM-DD 格式";
                return false;
            }
            if (entry["items"] is not JsonArray itemNodes || itemNodes.Count is < 1 or > 32)
            {
                error = "items 必须是包含 1 至 32 条文本的数组";
                return false;
            }
            var items = new List<string>(itemNodes.Count);
            foreach (JsonNode? itemNode in itemNodes)
            {
                string text = itemNode?.ToString()?.Trim() ?? "";
                if (text.Length is < 1 or > 512 || text.Contains('<') || text.Contains('>'))
                {
                    error = "更新记录文本长度或内容无效";
                    return false;
                }
                items.Add(text);
            }
            parsed.Add(new PluginChangelogEntry(version, date, items));
        }
        changelog = parsed;
        return true;
    }

    public static bool IsSafePluginName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value is "." or "..")
        {
            return false;
        }
        foreach (char ch in value)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>插件机器身份的规范格式；与仅用于兼容旧目录的路径安全名称分离。</summary>
    public static bool IsCanonicalPluginId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64
            || !IsCanonicalPluginIdCharacter(value[0])
            || !IsCanonicalPluginIdCharacter(value[^1]))
        {
            return false;
        }
        bool previousHyphen = false;
        foreach (char ch in value)
        {
            if (IsCanonicalPluginIdCharacter(ch))
            {
                previousHyphen = false;
                continue;
            }
            if (ch != '-' || previousHyphen)
            {
                return false;
            }
            previousHyphen = true;
        }
        return true;
    }

    private static bool IsCanonicalPluginIdCharacter(char ch)
    {
        return ch is >= 'a' and <= 'z' or >= '0' and <= '9';
    }

    internal static bool TryParseReplaces(
        JsonObject root,
        string currentName,
        out IReadOnlyList<string> replaces,
        out string? error,
        bool requireCanonicalNames = false)
    {
        replaces = Array.Empty<string>();
        error = null;
        JsonNode? node = root["replaces"];
        if (node is null)
        {
            return true;
        }
        if (node is not JsonArray array || array.Count > 8)
        {
            error = "必须是最多包含 8 个名称的数组";
            return false;
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? item in array)
        {
            string name = item?.ToString()?.Trim() ?? "";
            if ((!requireCanonicalNames ? !IsSafePluginName(name) : !IsCanonicalPluginId(name))
                || string.Equals(name, currentName, StringComparison.OrdinalIgnoreCase)
                || !names.Add(name))
            {
                error = $"包含不安全、重复或等于当前名称的插件名：{name}";
                return false;
            }
        }
        replaces = names.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        return true;
    }

    public static bool TryParseVersion(string? value, out PluginVersion version)
    {
        version = default;
        string[] parts = (value ?? "").Trim().Split('.', StringSplitOptions.None);
        if (parts.Length != 3
            || parts.Any(part => string.IsNullOrEmpty(part)
                || (part.Length > 1 && part[0] == '0')
                || part.Any(ch => ch is < '0' or > '9'))
            || !int.TryParse(parts[0], out int major)
            || !int.TryParse(parts[1], out int minor)
            || !int.TryParse(parts[2], out int patch))
        {
            return false;
        }
        version = new PluginVersion(major, minor, patch);
        return true;
    }

    public static int CompareVersions(string left, string right)
    {
        return TryParseVersion(left, out PluginVersion leftVersion)
            && TryParseVersion(right, out PluginVersion rightVersion)
            ? leftVersion.CompareTo(rightVersion)
            : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCompatible(PluginCatalogEntry entry, string hostVersion, out string reason)
    {
        if (!TryParseVersion(hostVersion, out PluginVersion host)
            || !TryParseVersion(entry.MinHostVersion, out PluginVersion minimum))
        {
            reason = "宿主版本或插件最低版本无效";
            return false;
        }
        if (host.CompareTo(minimum) < 0)
        {
            reason = $"需要宿主 v{entry.MinHostVersion} 或更高版本";
            return false;
        }
        if (entry.Kind == "managed-code"
            && (!TryParseApiVersion(entry.ApiVersion, out int apiMajor, out int apiMinor)
                || apiMajor != PluginApiVersion.Major
                || apiMinor > PluginApiVersion.Minor))
        {
            reason = $"需要兼容 Plugin API v{PluginApiVersion.Major}.{PluginApiVersion.Minor} 的版本（插件声明 v{entry.ApiVersion}）";
            return false;
        }
        reason = "";
        return true;
    }

    public static bool TryParseApiVersion(string? value, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        string[] parts = (value ?? "").Trim().Split('.', StringSplitOptions.None);
        if (parts.Length != 2
            || parts.Any(part => string.IsNullOrEmpty(part)
                || (part.Length > 1 && part[0] == '0')
                || part.Any(ch => ch is < '0' or > '9'))
            || !int.TryParse(parts[0], out major)
            || !int.TryParse(parts[1], out minor))
        {
            return false;
        }
        return major >= 0 && minor >= 0;
    }

    private static string RequiredString(JsonObject root, string property)
    {
        string value = root[property]?.ToString()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"缺少 {property}");
        }
        return value;
    }

    private static int RequiredInt(JsonObject root, string property)
    {
        if (root[property] is null)
        {
            throw new InvalidDataException($"缺少 {property}");
        }
        return root[property]!.GetValue<int>();
    }

    private static long RequiredLong(JsonObject root, string property)
    {
        if (root[property] is null)
        {
            throw new InvalidDataException($"缺少 {property}");
        }
        return root[property]!.GetValue<long>();
    }
}

internal sealed record PluginCatalog(
    int SchemaVersion,
    string Repository,
    string GeneratedAt,
    IReadOnlyList<PluginCatalogEntry> Plugins);

internal sealed record PluginCatalogEntry(
    string Name,
    string DisplayName,
    string GameName,
    string Description,
    string Version,
    string Kind,
    string ApiVersion,
    IReadOnlyList<string> Capabilities,
    string MinHostVersion,
    string PackageUrl,
    string Sha256,
    long SizeBytes,
    IReadOnlyList<string>? Replaces = null)
{
    public string ArtifactName { get; init; } = "";

    public int CatalogSchemaVersion { get; init; } = PluginRepositoryCatalog.SchemaVersion;

    public IReadOnlyList<PluginChangelogEntry> Changelog { get; init; } = Array.Empty<PluginChangelogEntry>();

    [JsonIgnore]
    public IReadOnlyList<string> ReplacementNames => Replaces ?? Array.Empty<string>();
}

internal sealed record PluginChangelogEntry(
    string Version,
    string Date,
    IReadOnlyList<string> Items);

internal readonly record struct PluginVersion(int Major, int Minor, int Patch) : IComparable<PluginVersion>
{
    public int CompareTo(PluginVersion other)
    {
        int major = Major.CompareTo(other.Major);
        if (major != 0) return major;
        int minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }
}
