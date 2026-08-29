using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.App.Contracts;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Cli;

/// <summary>CLI/菜单侧到常驻服务的 HTTP 通道工具（从 Program 提取，Program 与 DispatchMenu 共用，
/// 消除「manage 菜单进程内直调 vs CLI 走 HTTP」双执行通道割裂——菜单调度统一经常驻服务执行，Web 端可见）。</summary>
internal static class CliTransport
{
    public static readonly TimeSpan DefaultControlTimeout = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan NotificationTestTimeout = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan UpdateCheckTimeout = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan PluginRepositoryTimeout = TimeSpan.FromMinutes(2);

    /// <summary>确保常驻服务可达：探测失败时自动拉起服务进程并等待（最多 30 秒）。轻量模式同样提供仅本机的 Control API。</summary>
    public static int? EnsureService()
    {
        int port = RuntimeContext.Instance.Settings.WebPort;
        int? existing = FindServicePort(port);
        if (existing is not null)
        {
            return existing;
        }
        CliOutput.WriteDiagnostic($"[提示] 常驻服务未运行，正在自动拉起（端口 {port}）...");
        try
        {
            string exePath = Environment.ProcessPath ?? "";
            if (string.IsNullOrWhiteSpace(exePath))
            {
                CliOutput.WriteDiagnostic("[错误] 无法确定 NexusPipeline 程序路径");
                return null;
            }
            Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            CliOutput.WriteDiagnostic($"[错误] 自动拉起常驻服务失败：{ex.Message}");
            return null;
        }
        DateTime deadline = DateTime.Now.AddSeconds(30);
        while (DateTime.Now < deadline)
        {
            Thread.Sleep(500);
            int? discovered = FindServicePort(port);
            if (discovered is not null)
            {
                return discovered;
            }
        }
        CliOutput.WriteDiagnostic("[错误] 自动拉起常驻服务后仍无法连接（请查看管理器日志确认服务状态）。");
        return null;
    }

    /// <summary>只探测已有服务，不自动启动。优先读取实际端口标记，再探测配置端口及其漂移范围。</summary>
    public static int? FindServicePort(int configuredPort)
    {
        foreach (int port in CandidatePorts(configuredPort))
        {
            int? actual = ProbeActualPort(port, 250);
            if (actual is not null)
            {
                return actual;
            }
        }
        return null;
    }

    internal static IEnumerable<int> CandidatePorts(int configuredPort)
    {
        return CandidatePorts(configuredPort, AppPaths.WebPortPath, AppPaths.LegacyWebPortPath);
    }

    internal static IEnumerable<int> CandidatePorts(int configuredPort, string markedPath, string legacyMarkedPath)
    {
        var seen = new HashSet<int>();
        foreach (int marked in ReadMarkedPorts(markedPath, legacyMarkedPath))
        {
            if (marked is >= 1024 and <= 65535 && seen.Add(marked))
            {
                yield return marked;
            }
        }
        for (int offset = 0; offset < 20; offset++)
        {
            int port = configuredPort + offset;
            if (port is >= 1024 and <= 65535 && seen.Add(port))
            {
                yield return port;
            }
        }
    }

    private static IEnumerable<int> ReadMarkedPorts(string markedPath, string legacyMarkedPath)
    {
        var ports = new List<int>();
        foreach (string path in new[] { markedPath, legacyMarkedPath })
        {
            try
            {
                if (File.Exists(path)
                    && int.TryParse(File.ReadAllText(path), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int marked))
                {
                    ports.Add(marked);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"读取 Web 实际端口标记失败：{ex.Message}");
            }
        }
        return ports;
    }

    private static int? ProbeActualPort(int port, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            using HttpResponseMessage response = client.GetAsync($"http://127.0.0.1:{port}/api/status", cts.Token).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            JsonNode? node = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            if (!IsCompatibleStatus(node))
            {
                return null;
            }
            return int.TryParse(node?["actualPort"]?.ToString(), out int actual)
                && actual is >= 1024 and <= 65535
                ? actual
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>GET /api/status 探测 NexusPipeline 服务身份与可达性。</summary>
    public static bool Probe(int port, int timeoutMs)
    {
        return ProbeActualPort(port, timeoutMs) is not null;
    }

    internal static bool IsCompatibleStatus(JsonNode? node)
    {
        return string.Equals(node?["service"]?.ToString(), ControlApiContract.ServiceName, StringComparison.Ordinal)
            && int.TryParse(node?["controlApiVersion"]?.ToString(), out int version)
            && version == ControlApiContract.Version
            && int.TryParse(node?["actualPort"]?.ToString(), out int actualPort)
            && actualPort is >= 1024 and <= 65535;
    }

    internal static TimeSpan TimeoutFor(string method, string apiPath)
    {
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
            && string.Equals(apiPath, "/api/settings/test", StringComparison.OrdinalIgnoreCase))
        {
            return NotificationTestTimeout;
        }
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
            && string.Equals(apiPath, "/api/update/check", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateCheckTimeout;
        }
        if (apiPath.StartsWith("/api/plugins/store", StringComparison.OrdinalIgnoreCase))
        {
            return PluginRepositoryTimeout;
        }
        return DefaultControlTimeout;
    }

    /// <summary>读取响应体 {error} 字段（失败时回退原文/状态码）。</summary>
    public static string ReadError(HttpResponseMessage resp)
    {
        try
        {
            string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonNode.Parse(text)?["error"]?.ToString() ?? text;
        }
        catch
        {
            return $"服务返回错误（HTTP {(int)resp.StatusCode}）";
        }
    }

    /// <summary>POST JSON 到常驻服务 API；超时按请求类型采用显式策略。</summary>
    public static HttpResponseMessage Post(int port, string apiPath, object body)
    {
        JsonNode? node = JsonSerializer.SerializeToNode(body, JsonOpts.Default);
        return Send(port, "POST", apiPath, node);
    }

    /// <summary>GET 常驻服务 API；状态、CRUD 和运行轮询使用短超时。</summary>
    public static HttpResponseMessage Get(int port, string apiPath)
    {
        return Send(port, "GET", apiPath, null);
    }

    /// <summary>向常驻服务发送 JSON 请求，供正式 CLI 与交互菜单共用。</summary>
    public static HttpResponseMessage Send(int port, string method, string apiPath, JsonNode? body)
    {
        using var client = new HttpClient { Timeout = TimeoutFor(method, apiPath) };
        using var request = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{port}{apiPath}");
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(JsonOpts.Web), Encoding.UTF8, "application/json");
        }
        return client.SendAsync(request).GetAwaiter().GetResult();
    }
}
