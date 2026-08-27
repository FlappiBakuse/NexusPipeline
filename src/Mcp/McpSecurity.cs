using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace NexusPipeline.Mcp;

/// <summary>内嵌 MCP HTTP 的 loopback、Host、Origin 与请求体安全边界。</summary>
internal static class McpSecurity
{
    public const int MaxRequestBodyBytes = 2 * 1024 * 1024;

    public static string? Validate(HttpContext context, int port)
    {
        string host = NormalizeHost(context.Request.Host.Host);
        if (!IsLoopbackHost(host))
        {
            return "MCP 仅接受 loopback Host";
        }
        if (context.Request.Host.Port is int hostPort && hostPort != port)
        {
            return "MCP Host 端口不匹配";
        }
        if (context.Request.ContentLength is long length && length > MaxRequestBodyBytes)
        {
            return "MCP 请求体超过大小限制";
        }
        if (context.Request.Headers.TryGetValue("Origin", out Microsoft.Extensions.Primitives.StringValues origins)
            && !string.IsNullOrWhiteSpace(origins.ToString()))
        {
            string origin = origins.ToString().Trim();
            if (!IsAllowedOrigin(origin, host, port))
            {
                return "MCP Origin 不在 loopback 允许列表中";
            }
        }
        return null;
    }

    public static async Task RejectAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = "mcp_request_rejected",
            message,
        })).ConfigureAwait(false);
    }

    private static bool IsAllowedOrigin(string origin, string requestHost, int port)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || !IsLoopbackHost(NormalizeHost(uri.Host))
            || uri.Port != port)
        {
            return false;
        }
        return string.Equals(NormalizeHost(uri.Host), requestHost, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoopbackHost(string host)
    {
        return host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHost(string host)
    {
        return host.Trim().TrimStart('[').TrimEnd(']');
    }
}
