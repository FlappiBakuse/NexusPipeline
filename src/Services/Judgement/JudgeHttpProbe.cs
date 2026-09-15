using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusPipeline.Models;
using NexusPipeline.Services.Networking;

namespace NexusPipeline.Services;

/// <summary>判定脚本的只读 HTTP GET 投影。</summary>
internal sealed record JudgeHttpProbeResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("error")] string? Error = null);

internal static class JudgeHttpProbe
{
    internal const int MaxUrlLength = 2048;
    internal const int DefaultTimeoutMilliseconds = 10_000;
    internal const int MaxTimeoutMilliseconds = 15_000;
    internal const int MaxResponseBytes = 2 * 1024 * 1024;

    internal static string RunSync(
        string? url,
        string? optionsJson,
        OutboundHttpClientProvider? provider,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return RunAsync(url, optionsJson, provider, cancellationToken)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
            return Serialize(new JudgeHttpProbeResponse(false, 0, "", false, "timeout"));
        }
        catch (Exception)
        {
            return Serialize(new JudgeHttpProbeResponse(false, 0, "", false, "network_error"));
        }
    }

    internal static async Task<string> RunAsync(
        string? url,
        string? optionsJson,
        OutboundHttpClientProvider? provider,
        CancellationToken cancellationToken = default)
    {
        if (!TryCreateUri(url, out Uri? uri))
        {
            return Serialize(new JudgeHttpProbeResponse(false, 0, "", false, "invalid_url"));
        }

        (TimeSpan timeout, int maxBytes) = ReadOptions(optionsJson);
        provider ??= new OutboundHttpClientProvider(() => new AppSettings());
        using HttpClient client = provider.CreateClient(uri, timeout, allowAutoRedirect: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(timeout);
        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestTimeout.Token).ConfigureAwait(false);
            byte[] bytes = await ReadAtMostAsync(response, maxBytes, requestTimeout.Token).ConfigureAwait(false);
            bool truncated = bytes.Length > maxBytes;
            if (truncated)
            {
                bytes = bytes[..maxBytes];
            }
            string body = Encoding.UTF8.GetString(bytes);
            return Serialize(new JudgeHttpProbeResponse(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                body,
                truncated));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Serialize(new JudgeHttpProbeResponse(false, 0, "", false, "timeout"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return Serialize(new JudgeHttpProbeResponse(false, 0, "", false, "network_error"));
        }
        catch (IOException)
        {
            return Serialize(new JudgeHttpProbeResponse(false, 0, "", false, "network_error"));
        }
        catch (ObjectDisposedException)
        {
            return Serialize(new JudgeHttpProbeResponse(false, 0, "", false, "network_error"));
        }
    }

    internal static bool TryCreateUri(string? url, out Uri? uri)
    {
        uri = null;
        string value = url?.Trim() ?? "";
        if (value.Length == 0
            || value.Length > MaxUrlLength
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }
        uri = parsed;
        return true;
    }

    private static (TimeSpan Timeout, int MaxBytes) ReadOptions(string? optionsJson)
    {
        int timeoutMs = DefaultTimeoutMilliseconds;
        int maxBytes = MaxResponseBytes;
        try
        {
            if (!string.IsNullOrWhiteSpace(optionsJson) && optionsJson.Length <= 4096)
            {
                using JsonDocument document = JsonDocument.Parse(optionsJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (document.RootElement.TryGetProperty("timeoutMs", out JsonElement timeout)
                        && timeout.TryGetInt32(out int requestedTimeout))
                    {
                        timeoutMs = Math.Clamp(requestedTimeout, 1, MaxTimeoutMilliseconds);
                    }
                    if (document.RootElement.TryGetProperty("maxBytes", out JsonElement bytes)
                        && bytes.TryGetInt32(out int requestedBytes))
                    {
                        maxBytes = Math.Clamp(requestedBytes, 1, MaxResponseBytes);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Bad optional settings use the documented defaults; URL and network errors stay distinguishable.
        }
        return (TimeSpan.FromMilliseconds(timeoutMs), maxBytes);
    }

    private static async Task<byte[]> ReadAtMostAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(maxBytes + 1, 64 * 1024));
        byte[] buffer = new byte[8192];
        while (output.Length <= maxBytes)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            output.Write(buffer, 0, read);
            if (output.Length > maxBytes)
            {
                break;
            }
        }
        return output.ToArray();
    }

    private static string Serialize(JudgeHttpProbeResponse response) =>
        JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
}
