using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Serialization;
using NexusPipeline.Shared.Versioning;

namespace NexusPipeline.Modules.Updates;

/// <summary>更新策略文件的固定位置。桥接版本发布时保持文件有效；破坏性版本发布前追加 barrier。</summary>
internal static class UpdatePolicy
{
    public const int SchemaVersion = 1;
    public const int MaxBarriers = 256;
    public const int MaxCodeLength = 64;
    public const long MaxPolicyBytes = 256 * 1024;
    public const string Repository = "FlappiBakuse/NexusPipeline";
    public const string DefaultPolicyUrl = "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline/main/update-policy.json";

    private static readonly Regex CodePattern = new(
        "^[a-z0-9][a-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.Compiled);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private sealed record CachedPolicy(string Body, string ETag, DateTimeOffset? LastModified);

    public static Uri ResolveUri(UpdateSourcePolicy sourcePolicy)
    {
        return sourcePolicy.IsDefaultSource
            ? new Uri(DefaultPolicyUrl, UriKind.Absolute)
            : new Uri(sourcePolicy.SourceUri, "update-policy.json");
    }

    public static bool TryParse(string json, out UpdatePolicyDocument? policy, out string? error)
    {
        policy = null;
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "update-policy.json 为空";
                return false;
            }
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                error = "update-policy.json 不是 JSON 对象";
                return false;
            }
            int schemaVersion = RequiredInt(root, "schemaVersion");
            if (schemaVersion != SchemaVersion)
            {
                error = $"不支持的 update-policy schemaVersion：{schemaVersion}";
                return false;
            }
            string repository = RequiredString(root, "repository");
            if (!string.Equals(repository, Repository, StringComparison.Ordinal))
            {
                error = $"更新策略仓库标识不受信任：{repository}";
                return false;
            }
            if (root["barriers"] is not JsonArray barrierNodes)
            {
                error = "update-policy.json 缺少 barriers 数组";
                return false;
            }
            if (barrierNodes.Count > MaxBarriers)
            {
                error = $"更新屏障数量超过上限（{MaxBarriers}）";
                return false;
            }

            var barriers = new List<UpdateBarrier>(barrierNodes.Count);
            var versions = new HashSet<NexusVersion>();
            for (int index = 0; index < barrierNodes.Count; index++)
            {
                if (barrierNodes[index] is not JsonObject item)
                {
                    error = "更新屏障必须是 JSON 对象";
                    return false;
                }
                string versionText = RequiredString(item, "version");
                if (!NexusVersion.TryParse(versionText, out NexusVersion version)
                    || !string.Equals(versionText, version.ToString(), StringComparison.Ordinal)
                    || !versions.Add(version))
                {
                    error = $"更新屏障版本无效或重复：{versionText}";
                    return false;
                }
                if (index > 0 && barriers[index - 1].Version.CompareTo(version) >= 0)
                {
                    error = "更新屏障必须按版本从旧到新排列";
                    return false;
                }
                string code = RequiredString(item, "code");
                if (code.Length > MaxCodeLength || !CodePattern.IsMatch(code))
                {
                    error = $"更新屏障 code 无效：{code}";
                    return false;
                }
                string migrationUrl = item["migrationUrl"]?.ToString()?.Trim() ?? "";
                if (migrationUrl.Length > 0 && !IsValidMigrationUrl(migrationUrl))
                {
                    error = $"更新屏障 migrationUrl 必须是 HTTPS 地址：{migrationUrl}";
                    return false;
                }
                barriers.Add(new UpdateBarrier(version, code, migrationUrl.Length == 0 ? null : migrationUrl));
            }

            policy = new UpdatePolicyDocument(schemaVersion, repository, barriers);
            return true;
        }
        catch (Exception ex)
        {
            error = $"update-policy.json 解析失败：{ex.Message}";
            return false;
        }
    }

    public static UpdateBarrier? FindBarrier(UpdatePolicyDocument policy, NexusVersion current, NexusVersion target)
    {
        foreach (UpdateBarrier barrier in policy.Barriers)
        {
            if (barrier.Version.CompareTo(current) > 0 && barrier.Version.CompareTo(target) <= 0)
            {
                return barrier;
            }
        }
        return null;
    }

    public static async Task<UpdatePolicyFetchResult> FetchAsync(
        UpdateSourcePolicy sourcePolicy,
        HttpClient http,
        string userAgent,
        string cacheDirectory,
        CancellationToken token)
    {
        Uri policyUri = ResolveUri(sourcePolicy);
        CachedPolicy? cached = ReadCache(cacheDirectory, policyUri);
        try
        {
            using HttpResponseMessage response = await sourcePolicy.GetAsync(
                http,
                policyUri,
                UpdateResourceKind.Policy,
                userAgent,
                token,
                request => AddConditionalHeaders(request, cached)).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                string? cachedError = null;
                if (cached is null)
                {
                    return UpdatePolicyFetchResult.Unavailable("更新策略返回 304，但本地缓存不可用");
                }
                if (!TryParse(cached.Body, out UpdatePolicyDocument? cachedPolicy, out cachedError))
                {
                    return UpdatePolicyFetchResult.Unavailable(cachedError ?? "更新策略返回 304，但本地缓存无效");
                }
                return UpdatePolicyFetchResult.Success(cachedPolicy!);
            }
            if (!response.IsSuccessStatusCode)
            {
                return UpdatePolicyFetchResult.Unavailable($"更新策略请求失败：HTTP {(int)response.StatusCode}");
            }

            string json = await ReadBoundedUtf8Async(response.Content, token).ConfigureAwait(false);
            if (!TryParse(json, out UpdatePolicyDocument? policy, out string? error))
            {
                return UpdatePolicyFetchResult.Unavailable(error ?? "更新策略无效");
            }
            TryWriteCache(cacheDirectory, policyUri, json, response);
            return UpdatePolicyFetchResult.Success(policy!);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UpdatePolicyFetchResult.Unavailable(ex.Message);
        }
    }

    private static async Task<string> ReadBoundedUtf8Async(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength is > MaxPolicyBytes)
        {
            throw new InvalidDataException($"更新策略超过大小上限（{MaxPolicyBytes} 字节）");
        }
        await using Stream stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > MaxPolicyBytes)
            {
                throw new InvalidDataException($"更新策略超过大小上限（{MaxPolicyBytes} 字节）");
            }
            buffer.Write(chunk, 0, read);
        }
        return StrictUtf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static bool IsValidMigrationUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Fragment);
    }

    private static void AddConditionalHeaders(HttpRequestMessage request, CachedPolicy? cached)
    {
        if (cached is null)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(cached.ETag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
        }
        if (cached.LastModified is not null)
        {
            request.Headers.IfModifiedSince = cached.LastModified;
        }
    }

    private static CachedPolicy? ReadCache(string directory, Uri policyUri)
    {
        string path = CachePath(directory);
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            {
                return null;
            }
            if (!string.Equals(root["url"]?.ToString(), policyUri.AbsoluteUri, StringComparison.Ordinal))
            {
                return null;
            }
            string body = root["body"]?.ToString() ?? "";
            if (body.Length == 0)
            {
                return null;
            }
            DateTimeOffset? lastModified = DateTimeOffset.TryParse(root["lastModified"]?.ToString(), out DateTimeOffset parsed)
                ? parsed
                : null;
            return new CachedPolicy(
                body,
                root["etag"]?.ToString() ?? "",
                lastModified);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[更新] 读取 update-policy 缓存失败：{ex.Message}");
            return null;
        }
    }

    private static void TryWriteCache(string directory, Uri policyUri, string body, HttpResponseMessage response)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string? etag = response.Headers.ETag?.ToString();
            DateTimeOffset? lastModified = response.Content.Headers.LastModified;
            var root = new JsonObject
            {
                ["url"] = policyUri.AbsoluteUri,
                ["etag"] = etag ?? "",
                ["lastModified"] = lastModified?.ToString("O"),
                ["body"] = body,
            };
            JsonUtil.WriteAtomic(CachePath(directory), root.ToJsonString(JsonOpts.Indented));
        }
        catch (Exception ex)
        {
            // 缓存仅用于后续 304 校验；本次已经通过网络取得并验证的策略仍然有效。
            Logger.Warn($"[更新] 写入 update-policy 缓存失败：{ex.Message}");
        }
    }

    private static string CachePath(string directory) => Path.Combine(directory, "update-policy-cache.json");

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
}

internal sealed record UpdatePolicyDocument(
    int SchemaVersion,
    string Repository,
    IReadOnlyList<UpdateBarrier> Barriers);

internal sealed record UpdateBarrier(
    NexusVersion Version,
    string Code,
    string? MigrationUrl);

internal sealed record UpdatePolicyFetchResult(
    bool Verified,
    UpdatePolicyDocument? Policy,
    string? Error)
{
    public static UpdatePolicyFetchResult Success(UpdatePolicyDocument policy) => new(true, policy, null);

    public static UpdatePolicyFetchResult Unavailable(string error) => new(false, null, error);
}
