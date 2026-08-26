using System.Text.Json.Nodes;
using NexusPipeline.Persistence;

namespace NexusPipeline.Services.Update;

/// <summary>
/// 更新清单：SemVer 解析比较、GitHub Releases API JSON 解析与渠道过滤。纯逻辑，便于单测。
/// 资产约定：release 必须同时携带 NexusPipeline-v{ver}-win-x64.zip 与同名 .sha256 才能被发现。
/// </summary>
internal static class UpdateCatalog
{
    public const string DefaultSourceUrl = "https://api.github.com/repos/FlappiBakuse/NexusPipeline/releases";

    /// <summary>下载包尺寸上限（默认 200 MB，逐块校验）。</summary>
    public const long MaxDownloadBytes = 200L * 1024 * 1024;

    public static bool TryParseTag(string? tag, out (int Major, int Minor, int Patch) version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }
        string text = tag.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            text = text[1..];
        }
        string[] parts = text.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }
        if (!int.TryParse(parts[0], out int major)
            || !int.TryParse(parts[1], out int minor)
            || !int.TryParse(parts[2], out int patch))
        {
            return false;
        }
        version = (major, minor, patch);
        return true;
    }

    public static int Compare((int Major, int Minor, int Patch) left, (int Major, int Minor, int Patch) right)
    {
        int major = left.Major.CompareTo(right.Major);
        if (major != 0)
        {
            return major;
        }
        int minor = left.Minor.CompareTo(right.Minor);
        return minor != 0 ? minor : left.Patch.CompareTo(right.Patch);
    }

    /// <summary>
    /// 从 GitHub Releases API JSON 中按渠道挑选最高版本：跳过 draft；stable 渠道只见非 pre-release；
    /// 资产 zip+sha256 必须齐全；版本须高于当前版本。
    /// </summary>
    public static ReleaseInfo? PickRelease(JsonNode? root, string channel, (int Major, int Minor, int Patch) currentVersion)
    {
        if (root is not JsonArray releases)
        {
            return null;
        }
        ReleaseInfo? best = null;
        foreach (JsonNode? node in releases)
        {
            if (node is not JsonObject release)
            {
                continue;
            }
            if (release["draft"]?.GetValue<bool>() == true)
            {
                continue;
            }
            if (channel == "stable" && release["prerelease"]?.GetValue<bool>() == true)
            {
                continue;
            }
            string? tag = release["tag_name"]?.ToString();
            if (!TryParseTag(tag, out (int Major, int Minor, int Patch) version))
            {
                continue;
            }
            if (Compare(version, currentVersion) <= 0)
            {
                continue;
            }
            string? zipUrl = null;
            string? shaUrl = null;
            if (release["assets"] is JsonArray assets)
            {
                string zipName = AppPaths.UpdatePackageZipName(Format(version));
                string shaName = AppPaths.UpdatePackageShaName(Format(version));
                foreach (JsonNode? asset in assets)
                {
                    if (asset is not JsonObject item)
                    {
                        continue;
                    }
                    string? name = item["name"]?.ToString();
                    string? url = item["browser_download_url"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }
                    if (string.Equals(name, zipName, StringComparison.OrdinalIgnoreCase))
                    {
                        zipUrl = url;
                    }
                    else if (string.Equals(name, shaName, StringComparison.OrdinalIgnoreCase))
                    {
                        shaUrl = url;
                    }
                }
            }
            if (zipUrl is null || shaUrl is null)
            {
                continue;
            }
            bool prerelease = release["prerelease"]?.GetValue<bool>() == true;
            var candidate = new ReleaseInfo(version, tag!, release["name"]?.ToString() ?? "", release["body"]?.ToString() ?? "", prerelease, zipUrl, shaUrl);
            if (best is null || Compare(version, best.Version) > 0)
            {
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>更新源校验：必须是 https；回环 http（127.0.0.1/::1/localhost）为测试预留。</summary>
    public static string? ValidateSource(string? sourceUrl)
    {
        try
        {
            UpdateSourcePolicy policy = new(sourceUrl);
            return policy.ValidateManifestUri(policy.SourceUri);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            return "更新源地址无效";
        }
    }

    /// <summary>下载主机白名单：默认源只放行 api.github.com / github.com（release 资产实际落在 github.com）；
    /// 自定义/测试源只放行其自身主机（https 或回环 http）。</summary>
    public static bool IsAllowedHost(string? host, string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }
        try
        {
            return new UpdateSourcePolicy(sourceUrl).IsAllowedHost(host);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            return false;
        }
    }

    public static string Format((int Major, int Minor, int Patch) version)
    {
        return $"{version.Major}.{version.Minor}.{version.Patch}";
    }
}

/// <summary>候选发布信息（已通过渠道与资产完整性过滤）。</summary>
internal sealed record ReleaseInfo(
    (int Major, int Minor, int Patch) Version,
    string Tag,
    string Name,
    string Notes,
    bool Prerelease,
    string ZipUrl,
    string ShaUrl)
{
    public string VersionText => UpdateCatalog.Format(Version);
}
