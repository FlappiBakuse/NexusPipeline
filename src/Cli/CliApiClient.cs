using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Utilities;

namespace NexusPipeline.Cli;

internal sealed record CliApiResponse(
    bool Succeeded,
    int StatusCode,
    JsonNode? Body,
    string Code,
    string Message)
{
    public static CliApiResponse Success(int statusCode, JsonNode? body) =>
        new(true, statusCode, body, "ok", "");

    public static CliApiResponse Failure(int statusCode, JsonNode? body, string code, string message) =>
        new(false, statusCode, body, code, message);
}

/// <summary>CLI 到 owning service 的控制 API 客户端。CLI 进程不直接修改运行时数据。</summary>
internal sealed class CliApiClient
{
    public int? Port { get; private set; }

    public CliApiResponse Get(string path) => Send("GET", path, null);

    public CliApiResponse Post(string path, JsonNode? body = null) => Send("POST", path, body);

    public CliApiResponse Put(string path, JsonNode? body) => Send("PUT", path, body);

    public CliApiResponse Delete(string path, JsonNode? body = null) => Send("DELETE", path, body);

    private CliApiResponse Send(string method, string path, JsonNode? body)
    {
        if (Port is null)
        {
            Port = CliTransport.EnsureService();
            if (Port is null)
            {
                return CliApiResponse.Failure(503, null, "service_unavailable", "无法连接到常驻 NexusPipeline 服务");
            }
        }

        try
        {
            using HttpResponseMessage response = CliTransport.Send(Port.Value, method, path, body);
            string raw = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            JsonNode? node = Parse(raw);
            if (response.IsSuccessStatusCode)
            {
                return CliApiResponse.Success((int)response.StatusCode, node);
            }

            string serverCode = node?["code"]?.ToString() ?? MapCode((int)response.StatusCode);
            string message = node?["message"]?.ToString()
                ?? node?["error"]?.ToString()
                ?? (string.IsNullOrWhiteSpace(raw) ? $"服务返回 HTTP {(int)response.StatusCode}" : raw.Trim());
            return CliApiResponse.Failure((int)response.StatusCode, node, NormalizeCode(serverCode), message);
        }
        catch (Exception ex)
        {
            return CliApiResponse.Failure(503, null, "service_unavailable", $"控制 API 请求失败：{ex.Message}");
        }
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
            "execution_resource_in_use" or "busy" or "running" or "editing" or "locked" => "resource_busy",
            "not-ready" => "validation_error",
            "forbidden" => "operation_forbidden",
            _ => code,
        };
    }
}
