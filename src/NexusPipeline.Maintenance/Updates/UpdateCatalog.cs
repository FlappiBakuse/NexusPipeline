using System.Text.Json.Nodes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Versioning;

namespace NexusPipeline.Modules.Updates;

/// <summary>
/// 更新清单：受限 Nexus 版本解析比较、GitHub Releases API JSON 解析与渠道过滤。纯逻辑，便于单测。
/// 资产约定：release 必须携带完整 ZIP 及 GitHub 提供的 SHA256 digest、资产身份与尺寸。
/// </summary>
internal static class UpdateCatalog
{
    public const string DefaultSourceUrl = "https://api.github.com/repos/FlappiBakuse/NexusPipeline/releases";

    /// <summary>下载包尺寸上限（默认 200 MB，逐块校验）。</summary>
    public const long MaxDownloadBytes = 200L * 1024 * 1024;

    public static bool TryParseTag(string? tag, out NexusVersion version)
    {
        return NexusVersion.TryParseTag(tag, out version);
    }

    public static int Compare(NexusVersion left, NexusVersion right)
    {
        return left.CompareTo(right);
    }

    /// <summary>
    /// 从 GitHub Releases API JSON 中按渠道挑选最高版本：跳过 draft；Release 的 prerelease 标记
    /// 必须符合宿主项目发布策略；stable 渠道只见正式 Release；完整 ZIP 的服务端资产摘要必须有效；版本须高于当前版本。
    /// </summary>
    public static ReleaseInfo? PickRelease(JsonNode? root, string channel, NexusVersion currentVersion)
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
            if (release["draft"] is not JsonValue draft || !draft.TryGetValue<bool>(out bool isDraft) || isDraft)
            {
                continue;
            }
            string? tag = release["tag_name"]?.ToString();
            if (!TryParseTag(tag, out NexusVersion version))
            {
                continue;
            }
            if (release["prerelease"] is not JsonValue prerelease || !prerelease.TryGetValue<bool>(out bool declaredPrerelease)
                || release["id"] is not JsonValue releaseIdentity || !releaseIdentity.TryGetValue<long>(out long releaseId) || releaseId <= 0) continue;
            bool expectedPrerelease = NexusReleasePolicy.RequiresGitHubPrerelease(version);
            if (declaredPrerelease != expectedPrerelease)
            {
                continue;
            }
            if (!NexusReleasePolicy.IsVisibleInChannel(version, channel))
            {
                continue;
            }
            if (Compare(version, currentVersion) <= 0)
            {
                continue;
            }
            string portableName = $"NexusPipeline-v{Format(version)}-win-x64-Portable.zip";
            bool recipePackage = (release["assets"] as JsonArray)?.Any(item => item?["name"]?.ToString() == portableName) == true;
            string zipName = recipePackage ? portableName : AppPaths.UpdatePackageZipName(Format(version));
            JsonObject[] matching = (release["assets"] as JsonArray)?.OfType<JsonObject>()
                .Where(item => item["name"]?.ToString() == zipName).ToArray() ?? [];
            if (matching.Length != 1 || !TryReadAsset(matching[0], zipName, out UpdateReleaseAsset? asset)) continue;
            string manifestName = $"NexusPipeline-v{Format(version)}-win-x64-update.json";
            var assets = new Dictionary<string, UpdateReleaseAsset>(StringComparer.Ordinal);
            UpdateReleaseAsset? targetManifest = null;
            UpdateReleaseAsset? deltaAsset = null;
            if (recipePackage || (release["assets"] as JsonArray)?.Any(item => item?["name"]?.ToString() == manifestName) == true)
            {
                if (release["assets"] is not JsonArray declaredAssets || declaredAssets.Any(item => item is not JsonObject)) continue;
                bool valid = true;
                var ids = new HashSet<long>();
                foreach (JsonObject item in (release["assets"] as JsonArray)!.OfType<JsonObject>())
                {
                    string name = item["name"]?.ToString() ?? "";
                    if (!TryReadAsset(item, name, out var parsed) || !assets.TryAdd(name, parsed!) || !ids.Add(parsed!.Id)) { valid = false; break; }
                }
                if (!valid) continue;
                if (recipePackage)
                {
                    if (assets.Count != 3 || !assets.ContainsKey($"NexusPipeline-v{Format(version)}-win-x64-Setup.exe")
                        || !assets.TryGetValue($"NexusPipeline-v{Format(version)}-win-x64-Update.zip", out deltaAsset)) continue;
                }
                else if (!assets.TryGetValue(manifestName, out targetManifest) || targetManifest.SizeBytes > UpdateTargetManifest.MaxBytes
                    || !assets.ContainsKey($"NexusPipeline-v{Format(version)}-win-x64-setup.exe")
                    || UpdateTargetManifest.ComponentIds.Any(id => !assets.ContainsKey($"NexusPipeline-v{Format(version)}-win-x64-{id}.zip"))) continue;
            }
            var candidate = new ReleaseInfo(version, tag!, release["name"]?.ToString() ?? "", release["body"]?.ToString() ?? "",
                declaredPrerelease, releaseId, asset!, targetManifest, assets, DeltaAsset: deltaAsset);
            if (best is null || Compare(version, best.Version) > 0)
            {
                best = candidate;
            }
        }
        return best;
    }

    internal static bool TryReadAsset(JsonObject item, string expectedName, out UpdateReleaseAsset? asset)
    {
        asset = null;
        if (item["name"]?.ToString() != expectedName || item["state"]?.ToString() != "uploaded"
            || item["id"] is not JsonValue idValue || !idValue.TryGetValue<long>(out long id) || id <= 0
            || item["size"] is not JsonValue sizeValue || !sizeValue.TryGetValue<long>(out long size) || size <= 0 || size > MaxDownloadBytes
            || item["digest"] is not JsonValue digestValue || !digestValue.TryGetValue<string>(out string? digest)
            || digest is not { Length: 71 } || !digest.StartsWith("sha256:", StringComparison.Ordinal)
            || digest[7..].Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || item["browser_download_url"] is not JsonValue urlValue || !urlValue.TryGetValue<string>(out string? url)
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
        asset = new(id, expectedName, url!, size, digest[7..]);
        return true;
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

    public static string Format(NexusVersion version)
    {
        return version.ToString();
    }
}

/// <summary>候选发布信息（已通过渠道与资产完整性过滤）。</summary>
internal sealed record ReleaseInfo(
    NexusVersion Version,
    string Tag,
    string Name,
    string Notes,
    bool Prerelease,
    long ReleaseId,
    UpdateReleaseAsset ZipAsset,
    UpdateReleaseAsset? TargetManifestAsset = null,
    IReadOnlyDictionary<string, UpdateReleaseAsset>? Assets = null,
    UpdateTargetManifest? FrozenTarget = null,
    UpdateReleaseAsset? DeltaAsset = null,
    string? BaseManifestHint = null)
{
    public string VersionText => UpdateCatalog.Format(Version);
    public string ZipUrl => ZipAsset.Url;
}

internal sealed record UpdateReleaseAsset(long Id, string Name, string Url, long SizeBytes, string Sha256);
