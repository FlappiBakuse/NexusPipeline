using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Utilities;

namespace NexusPipeline.Cli;

/// <summary>CLI/菜单侧到常驻服务的 HTTP 通道工具（v0.6.6+ 从 Program 提取，Program 与 DispatchMenu 共用，
/// 消除「manage 菜单进程内直调 vs CLI 走 HTTP」双执行通道割裂——菜单调度统一经常驻服务执行，Web 端可见）。</summary>
internal static class CliTransport
{
    /// <summary>确保常驻服务可达：探测失败时轻量模式报错退出，否则自动拉起服务进程并等待（最多 30 秒）。返回实际端口或 null。</summary>
    public static int? EnsureService()
    {
        int port = RuntimeContext.Instance.Settings.WebPort;
        if (Probe(port, 2000))
        {
            return port;
        }
        if (RuntimeContext.Instance.Settings.LightweightMode)
        {
            Console.WriteLine("[错误] 服务处于轻量运行模式，未启动 Web 接口，无法提交任务");
            return null;
        }
        Console.WriteLine($"[提示] 常驻服务未运行，正在自动拉起（端口 {port}）...");
        try
        {
            string exePath = Environment.ProcessPath ?? "";
            Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 自动拉起常驻服务失败：{ex.Message}");
            return null;
        }
        DateTime deadline = DateTime.Now.AddSeconds(30);
        while (DateTime.Now < deadline)
        {
            Thread.Sleep(500);
            if (Probe(port, 2000))
            {
                return port;
            }
        }
        Console.WriteLine("[错误] 自动拉起常驻服务后仍无法连接（请查看管理器日志确认服务状态）。");
        return null;
    }

    /// <summary>GET /api/status 探测服务可达性（HTTP 2xx 视为可达）。</summary>
    public static bool Probe(int port, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            using HttpResponseMessage resp = client.GetAsync($"http://127.0.0.1:{port}/api/status", cts.Token).GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
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

    /// <summary>POST JSON 到常驻服务 API（5 秒超时）。</summary>
    public static HttpResponseMessage Post(int port, string apiPath, object body)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        return client.PostAsync($"http://127.0.0.1:{port}{apiPath}",
            new StringContent(JsonSerializer.Serialize(body, JsonOpts.Default), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
    }

    /// <summary>GET 常驻服务 API（5 秒超时）。</summary>
    public static HttpResponseMessage Get(int port, string apiPath)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        return client.GetAsync($"http://127.0.0.1:{port}{apiPath}").GetAwaiter().GetResult();
    }
}
