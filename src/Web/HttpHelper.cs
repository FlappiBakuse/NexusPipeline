using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Web;

internal static class HttpHelper
{
    public static void ServeFile(HttpListenerContext context, string filePath)
    {
        if (!File.Exists(filePath))
        {
            NotFoundAsync(context).GetAwaiter().GetResult();
            return;
        }
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        string contentType = extension switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
        context.Response.ContentType = contentType;
        // v0.6.9+（P13）：静态文件补安全头（nosniff / referrer 策略 / CSP——零 CDN 纯本地资源，img-src 允许 data: 内联图标）；
        // 缓存保持 no-cache（零构建无版本号，浏览器每次校验）。
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; connect-src 'self'; font-src 'self' data:";
        byte[] data = File.ReadAllBytes(filePath);
        context.Response.ContentLength64 = data.Length;
        context.Response.OutputStream.Write(data, 0, data.Length);
        context.Response.OutputStream.Close();
    }

    public static async Task WriteJsonAsync(HttpListenerContext context, object value, int statusCode = 200)
    {
        byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOpts.Web));
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = data.Length;
        await context.Response.OutputStream.WriteAsync(data).ConfigureAwait(false);
        context.Response.OutputStream.Close();
    }

    public static async Task NotFoundAsync(HttpListenerContext context)
    {
        await WriteJsonAsync(context, new { error = "未找到" }, 404).ConfigureAwait(false);
    }

    public static async Task MethodNotAllowedAsync(HttpListenerContext context)
    {
        await WriteJsonAsync(context, new { error = "请求方法不支持" }, 405).ConfigureAwait(false);
    }

    public static JsonNode? ParseBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(body);
        }
        catch
        {
            return null;
        }
    }

    public static T? ParseBody<T>(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return default;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOpts.Default);
        }
        catch
        {
            return default;
        }
    }
}
