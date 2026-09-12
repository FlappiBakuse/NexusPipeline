using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Models;
using NexusPipeline.Persistence;
using NexusPipeline.Plugins;
using NexusPipeline.Utilities;
using NexusPipeline.Web;
using Xunit;

namespace NexusPipeline.Tests;

/// <summary>
/// 插件自有 Web API 的生产传输契约：请求体按内容类型投影为 JsonBody 或 OpenBodyStream，
/// 二进制响应按受限 Content-Type 回传，超限请求在读取前拒绝。
/// </summary>
public sealed class PluginWebApiTransportTests
{
    [Fact]
    public void BinaryRequest_IsReadableThroughOpenBodyStream()
    {
        using var fixture = new WebApiPluginFixture();
        byte[] payload = Encoding.UTF8.GetBytes("fixture-binary-request-body");

        StubResponse response = fixture.Send(
            "POST",
            "binary/echo",
            payload,
            "application/octet-stream");

        Assert.Equal(200, response.StatusCode);
        JsonObject body = response.Json();
        Assert.Equal(payload.Length, body["length"]!.GetValue<long>());
        Assert.Equal("fixture-binary-request-body", body["text"]!.GetValue<string>());
        Assert.Equal("application/octet-stream", body["contentType"]!.GetValue<string>());
    }

    [Fact]
    public void JsonRequest_IsProjectedAsJsonBodyAndKeepsRawLength()
    {
        using var fixture = new WebApiPluginFixture();
        byte[] payload = Encoding.UTF8.GetBytes("{\"name\":\"外观\",\"count\":3}");

        StubResponse response = fixture.Send(
            "PUT",
            "json/echo",
            payload,
            "application/json; charset=utf-8");

        Assert.Equal(200, response.StatusCode);
        JsonObject body = response.Json();
        Assert.Equal(payload.Length, body["contentLength"]!.GetValue<long>());
        Assert.Equal("application/json", body["contentType"]!.GetValue<string>());
        JsonNode? projected = JsonNode.Parse(body["jsonBody"]!.GetValue<string>());
        Assert.Equal("外观", projected!["name"]!.GetValue<string>());
        Assert.Equal(3, projected["count"]!.GetValue<int>());
    }

    [Fact]
    public void BinaryResponse_IsServedWithTheDeclaredAllowedContentType()
    {
        using var fixture = new WebApiPluginFixture();

        StubResponse response = fixture.Send("GET", "binary/image");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("image/png", response.ContentType);
        Assert.Equal("no-store", response.Header("Cache-Control"));
        Assert.Equal("nosniff", response.Header("X-Content-Type-Options"));
        Assert.Equal("fixture-binary-payload", Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public void BinaryResponse_OutsideTheAllowedContentTypes_IsReportedAsPluginError()
    {
        using var fixture = new WebApiPluginFixture();

        StubResponse response = fixture.Send("GET", "binary/document");

        Assert.Equal(500, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.ContentType);
        Assert.Equal("plugin_error", response.Json()["code"]!.GetValue<string>());
    }

    [Fact]
    public void Request_AboveTheHostLimit_IsRejectedBeforeThePluginHandlerRuns()
    {
        using var fixture = new WebApiPluginFixture();

        StubResponse response = fixture.Send(
            "POST",
            "binary/echo",
            new byte[ApiPluginWebApiHandler.MaxRequestBytes + 1],
            "application/octet-stream");

        Assert.Equal(413, response.StatusCode);
        Assert.Equal("request_too_large", response.Json()["code"]!.GetValue<string>());
    }

    /// <summary>驱动真实 handler 的请求上下文：请求体直接来自内存，响应写回内存流。</summary>
    private sealed class StubResponse
    {
        private static readonly byte[] HeaderSeparator = "\r\n\r\n"u8.ToArray();

        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        public StubResponse(byte[] raw)
        {
            int headerEnd = FindHeaderEnd(raw);
            if (headerEnd < 0)
            {
                StatusCode = 0;
                Body = raw;
                return;
            }
            string head = Encoding.ASCII.GetString(raw, 0, headerEnd);
            string[] lines = head.Split("\r\n");
            StatusCode = int.Parse(lines[0].Split(' ')[1]);
            foreach (string line in lines.Skip(1))
            {
                int separator = line.IndexOf(':');
                if (separator > 0)
                {
                    _headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                }
            }
            Body = raw[(headerEnd + HeaderSeparator.Length)..];
        }

        public int StatusCode { get; }

        public IReadOnlyDictionary<string, string> Headers => _headers;

        public byte[] Body { get; }

        public string? ContentType => Header("Content-Type");

        public string? Header(string name) => _headers.TryGetValue(name, out string? value) ? value : null;

        public JsonObject Json() => JsonNode.Parse(Encoding.UTF8.GetString(Body))!.AsObject();

        private static int FindHeaderEnd(byte[] raw)
        {
            for (int index = 0; index <= raw.Length - HeaderSeparator.Length; index++)
            {
                if (raw.AsSpan(index, HeaderSeparator.Length).SequenceEqual(HeaderSeparator))
                {
                    return index;
                }
            }
            return -1;
        }
    }

    /// <summary>
    /// 安装一个真实 managed-code fixture 插件，让生产 PluginManager 注册插件 Web API 路由，
    /// 再把 WebContext 交给生产 handler 处理。
    /// </summary>
    private sealed class WebApiPluginFixture : IDisposable
    {
        private const string PluginName = "fixture-web-api";

        private readonly string _pluginDirectory;

        public WebApiPluginFixture()
        {
            const string artifactName = "FixtureWebApi";
            _pluginDirectory = Path.Combine(AppPaths.PluginsDir, artifactName);
            DeleteDirectory(_pluginDirectory);
            DeletePluginState();
            Directory.CreateDirectory(_pluginDirectory);
            string assemblyPath = typeof(NexusPipeline.TestPlugin.TestPlugin).Assembly.Location;
            File.Copy(assemblyPath, Path.Combine(_pluginDirectory, Path.GetFileName(assemblyPath)), overwrite: true);
            File.WriteAllText(Path.Combine(_pluginDirectory, "plugin.json"), $$"""
            {
              "schemaVersion": 2,
              "name": "{{PluginName}}",
              "artifactName": "{{artifactName}}",
              "displayName": "{{PluginName}}",
              "description": "managed fixture",
              "version": "0.1.0",
              "kind": "managed-code",
              "apiVersion": "1.0",
              "entryAssembly": "{{Path.GetFileName(assemblyPath)}}",
              "entryType": "{{typeof(NexusPipeline.TestPlugin.TestPlugin).FullName}}",
              "capabilities": ["background-jobs"]
            }
            """);
            AppSettings settings = RuntimeContext.Instance.Settings;
            settings.PluginPreferences ??= new Dictionary<string, PluginPreference>(StringComparer.OrdinalIgnoreCase);
            settings.PluginPreferences[PluginName] = new PluginPreference { Enabled = true };
            PluginManager manager = RuntimeContext.Instance.Plugins;
            manager.LoadAll();
            if (!manager.IsEnabled(PluginName))
            {
                throw new InvalidOperationException(
                    $"fixture 插件未启用，无法注册插件 Web API 路由：{manager.GetRuntimeState(PluginName)}/{manager.GetRuntimeError(PluginName)}");
            }
        }

        public StubResponse Send(string method, string route, byte[]? body = null, string? contentType = null)
        {
            byte[] payload = body ?? Array.Empty<byte>();
            var responseBuffer = new MemoryStream();
            var context = new WebContext(
                new NexusPipeline.Web.WebRequest(
                    new Uri($"http://127.0.0.1/api/plugin-api/{PluginName}/{route}"),
                    method,
                    new NameValueCollection(StringComparer.OrdinalIgnoreCase),
                    new NameValueCollection(StringComparer.OrdinalIgnoreCase),
                    new MemoryStream(payload, writable: false),
                    payload.Length,
                    contentType,
                    Encoding.UTF8,
                    payload.Length > 0,
                    new IPEndPoint(IPAddress.Loopback, 50000)),
                NexusPipeline.Web.WebResponse.ForManagedStream(responseBuffer));
            ApiPluginWebApiHandler.Handle(
                    context,
                    method,
                    new[] { "plugin-api", PluginName }.Concat(route.Split('/')).ToArray(),
                    "")
                .WaitAsync(TimeSpan.FromSeconds(15))
                .GetAwaiter()
                .GetResult();
            context.Response.Close();
            context.Response.OutputStream.Dispose();
            return new StubResponse(responseBuffer.ToArray());
        }

        public void Dispose()
        {
            try
            {
                RuntimeContext.Instance.Settings.PluginPreferences?.Remove(PluginName);
                RuntimeContext.Instance.Plugins.LoadAll();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[测试] 卸载 fixture 插件失败：{ex.Message}");
            }
            for (int attempt = 0; attempt < 5 && Directory.Exists(_pluginDirectory); attempt++)
            {
                try
                {
                    Directory.Delete(_pluginDirectory, recursive: true);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(50);
                }
            }
            DeletePluginState();
        }

        private static void DeletePluginState()
        {
            foreach (string path in new[]
            {
                Path.Combine(AppPaths.ConfigDir, "plugins", PluginName + ".json"),
                Path.Combine(AppPaths.ConfigDir, "plugins", PluginName + ".secrets.json"),
            })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private static void DeleteDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
