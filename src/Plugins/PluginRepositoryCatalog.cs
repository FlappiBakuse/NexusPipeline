using System.Text.Json.Nodes;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Plugins;

/// <summary>官方插件仓库 catalog.json 的严格解析与供应链字段校验。</summary>
internal static class PluginRepositoryCatalog
{
    public const int SchemaVersion = 1;
    public const string Repository = "FlappiBakuse/NexusPipeline-Plugins";
    public const string CatalogUrl = "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/catalog.json";
    public const string PackageUrlPrefix = "https://github.com/FlappiBakuse/NexusPipeline-Plugins/releases/download/";
    public const long MaxCatalogBytes = 2L * 1024 * 1024;
    public const long MaxPackageBytes = 200L * 1024 * 1024;
    public const int MaxEntries = 256;

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
            if (schemaVersion != SchemaVersion)
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
            foreach (JsonNode? node in pluginNodes)
            {
                if (node is not JsonObject item)
                {
                    error = "插件 catalog 条目必须是 JSON 对象";
                    return false;
                }
                string name = RequiredString(item, "name");
                if (!IsSafePluginName(name) || !names.Add(name))
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
                string? packageUrlError = ValidatePackageUrl(packageUrl);
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
                    sizeBytes));
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
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return "地址格式无效";
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.AbsolutePath.StartsWith("/FlappiBakuse/NexusPipeline-Plugins/releases/download/", StringComparison.Ordinal))
        {
            return "地址必须指向官方插件仓库的 GitHub Release 资产";
        }
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return "地址不允许包含 query 或 fragment";
        }
        string releasePath = uri.AbsolutePath["/FlappiBakuse/NexusPipeline-Plugins/releases/download/".Length..];
        string[] segments = releasePath.Split('/', StringSplitOptions.None);
        if (segments.Length != 2
            || !IsSafeUrlSegment(segments[0])
            || !IsSafeUrlSegment(segments[1])
            || !segments[1].EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return "地址缺少有效的 Release 资产名";
        }
        return null;
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

    public static bool TryParseApiMajor(string? value, out int major)
    {
        return TryParseApiVersion(value, out major, out _);
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
    long SizeBytes);

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
