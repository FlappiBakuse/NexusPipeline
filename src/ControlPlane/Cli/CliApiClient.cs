using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Shared.Localization;
using NexusPipeline.Shared.Logging;

namespace NexusPipeline.ControlPlane.Cli;

internal sealed record CliApiResponse(
    bool Succeeded,
    int StatusCode,
    JsonNode? Body,
    JsonNode? Args,
    string Code,
    string Message)
{
    public static CliApiResponse Success(int statusCode, JsonNode? body) =>
        new(true, statusCode, body, null, "ok", "");

    public static CliApiResponse Failure(int statusCode, JsonNode? body, JsonNode? args, string code, string message) =>
        new(false, statusCode, body, args, code, message);
}

/// <summary>CLI 到 owning service 的控制 API 客户端。CLI 进程不直接修改运行时数据。</summary>
internal sealed class CliApiClient
{
    public int? Port { get; private set; }

    internal string? ETag { get; private set; }

    public CliApiResponse Get(string path) => Send("GET", path, null);

    public CliApiResponse Post(string path, JsonNode? body = null) => Send("POST", path, body);

    public CliApiResponse Put(string path, JsonNode? body) => Send("PUT", path, body);

    public CliApiResponse Delete(string path, JsonNode? body = null) => Send("DELETE", path, body);

    internal CliApiResponse Download(string path, string destination)
    {
        try
        {
            string full = CliArtifactOutput.ValidateDestination(destination);
            Port ??= CliTransport.EnsureService();
            if (Port is null) return Failure(503, null, "service_unavailable");
            using var response = CliTransport.Get(Port.Value, path);
            if (!response.IsSuccessStatusCode)
            {
                var error = Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                var errorObject = error as JsonObject;
                string code = NormalizeCode(errorObject?["code"]?.ToString() ?? MapCode((int)response.StatusCode));
                return Failure((int)response.StatusCode, error, code, errorObject?["args"]?.DeepClone());
            }
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return CliApiResponse.Success(204, new JsonObject { ["available"] = false });
            byte[] bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            using var stream = new MemoryStream(bytes);
            CliArtifactOutput.SaveAsync(stream, full, bytes.Length, hash, maxBytes: 16 * 1024 * 1024).GetAwaiter().GetResult();
            return CliApiResponse.Success(200, new JsonObject { ["path"] = full, ["sizeBytes"] = bytes.Length,
                ["sha256"] = hash, ["contentType"] = response.Content.Headers.ContentType?.ToString() });
        }
        catch (TaskCanceledException)
        {
            return Failure(408, null, "timeout");
        }
        catch (HttpRequestException)
        {
            return Failure(503, null, "service_unavailable");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return CliApiResponse.Failure(400, null, null, "validation_error", e.Message);
        }
    }

    internal JsonNode CopyDiagnosticArtifact(JsonNode receipt, string destination)
    {
        if (Port is null || receipt["artifactId"]?.GetValue<string>() is not { } id || !Guid.TryParseExact(id, "N", out _)
            || receipt["sizeBytes"]?.GetValue<long>() is not { } size || receipt["sha256"]?.GetValue<string>() is not { } hash)
            throw new InvalidDataException("诊断包回执无效");
        using var deadline = new CancellationTokenSource(CliTransport.DefaultControlTimeout);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = CliTransport.DefaultControlTimeout };
        using var response = client.GetAsync($"http://127.0.0.1:{Port}/api/diagnostics/artifacts/{id}", HttpCompletionOption.ResponseHeadersRead,
            deadline.Token).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != "application/zip"
            || response.Content.Headers.ContentLength != size) throw new InvalidDataException("诊断包下载回执不符");
        using var stream = response.Content.ReadAsStream(deadline.Token);
        CliArtifactOutput.SaveAsync(stream, destination, size, hash, deadline.Token).GetAwaiter().GetResult();
        var result = receipt.DeepClone();
        result["path"] = Path.GetFullPath(destination);
        return result;
    }

    internal CliApiResponse Send(string method, string path, JsonNode? body, IReadOnlyDictionary<string, string>? headers = null,
        byte[]? bytes = null, string? contentType = null)
    {
        if (Port is null)
        {
            Port = CliTransport.EnsureService();
            if (Port is null)
            {
                return Failure(503, null, "service_unavailable");
            }
        }

        try
        {
            using HttpResponseMessage response = CliTransport.Send(Port.Value, method, path, body, LocaleContext.Current, headers, bytes, contentType);
            ETag = response.Headers.ETag?.ToString();
            string raw = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            JsonNode? node = Parse(raw);
            if (response.IsSuccessStatusCode)
            {
                if (!string.IsNullOrWhiteSpace(raw) && node is null)
                    return CliApiResponse.Failure((int)response.StatusCode, null, null, "validation_error", "响应不是 JSON；二进制下载请指定 --output");
                return CliApiResponse.Success((int)response.StatusCode, node);
            }

            string serverCode = node?["code"]?.ToString() ?? MapCode((int)response.StatusCode);
            string normalizedCode = NormalizeCode(serverCode);
            JsonNode? args = node?["args"]?.DeepClone();
            return Failure((int)response.StatusCode, node, normalizedCode, args);
        }
        catch (TaskCanceledException)
        {
            return Failure(408, null, "timeout");
        }
        catch (HttpRequestException)
        {
            return Failure(503, null, "service_unavailable");
        }
        catch (Exception ex)
        {
            Logger.Debug($"Control API request failed: {ex}");
            return Failure(503, null, "service_unavailable");
        }
    }

    private static CliApiResponse Failure(int statusCode, JsonNode? body, string code, JsonNode? args = null)
    {
        return CliApiResponse.Failure(
            statusCode,
            body,
            args,
            code,
            HostLocalization.TranslateApiError(code, args, statusCode, LocaleCatalog.HostLocale));
    }

    private static JsonNode? Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string MapCode(int statusCode)
    {
        return statusCode switch
        {
            400 => "validation_error",
            401 or 403 => "operation_forbidden",
            404 => "not_found",
            409 => "resource_busy",
            408 or 504 => "timeout",
            >= 500 => "internal_error",
            _ => "invalid_arguments",
        };
    }

    private static string NormalizeCode(string code)
    {
        return code switch
        {
            "execution_resource_in_use" or "host_maintenance" or "busy" or "running" or "editing" or "locked" => "resource_busy",
            "not-ready" => "validation_error",
            "forbidden" => "operation_forbidden",
            _ => code,
        };
    }
}
