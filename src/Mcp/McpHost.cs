using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NexusPipeline.Persistence;
using NexusPipeline.Utilities;

namespace NexusPipeline.Mcp;

/// <summary>
/// 与常驻进程同生命周期的 MCP Streamable HTTP 宿主。
/// 该宿主只监听 loopback，端口冲突不漂移，也不影响已有 Control API 继续运行。
/// </summary>
internal sealed class McpHost : IDisposable
{
    private readonly RuntimeContext _runtime;


    private readonly Func<bool>? _requestRestart;

    private WebApplication? _app;

    public McpHost(RuntimeContext runtime, Func<bool>? requestRestart)
    {
        _runtime = runtime;
        _requestRestart = requestRestart;
    }

    public static McpHost? Current { get; private set; }

    public int Port { get; private set; }

    public bool IsRunning { get; private set; }

    public string Endpoint => $"http://127.0.0.1:{Port}/mcp";

    public bool TryStart(int port)
    {
        if (port is < 1024 or > 65535)
        {
            Logger.Error($"[错误] MCP 端口无效：{port}（必须为 1024-65535），MCP 未启动。");
            return false;
        }

        Port = port;
        try
        {
            _app = BuildApp();
            _app.StartAsync().GetAwaiter().GetResult();
            IsRunning = true;
            Current = this;
            Logger.Info($"MCP 服务已启动：{Endpoint}");
            return true;
        }
        catch (Exception ex)
        {
            IsRunning = false;
            if (ReferenceEquals(Current, this))
            {
                Current = null;
            }
            DisposeApp();
            Logger.Error($"[错误] MCP 服务启动失败（端口 {port}，主服务继续运行）：{ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        IsRunning = false;
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
        if (_app is null)
        {
            return;
        }
        try
        {
            _app.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] MCP 服务停止异常：{ex.Message}");
        }
        finally
        {
            DisposeApp();
        }
        Logger.Info("MCP 服务已停止。");
    }

    public void Dispose() => Stop();

    private WebApplication BuildApp()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(McpHost).Assembly.GetName().Name,
            ContentRootPath = AppPaths.AppRoot,
        });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, Port);
            options.Limits.MaxRequestBodySize = McpSecurity.MaxRequestBodyBytes;
        });
        // Kestrel 的 HostFiltering 允许列表作为第一层配置；请求中间件再做端口与 Origin 精确校验。
        builder.WebHost.UseSetting("allowedHosts", "127.0.0.1;localhost;[::1]");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new McpToolContext(_runtime, _requestRestart));

        IMcpServerBuilder mcp = builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation
            {
                Name = "NexusPipeline",
                Version = typeof(McpHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            };
            options.ServerInstructions = "NexusPipeline 本地自动化控制面。长时间运行操作会立即返回 runId，请使用 get_run 轮询；破坏性操作请通过本地 CLI 执行。";
        });
        mcp.WithHttpTransport(options => options.Stateless = true);
        mcp.WithTools<McpReadOnlyTools>();
        mcp.WithTools<McpMutationTools>();

        WebApplication app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/mcp"))
            {
                string? securityError = McpSecurity.Validate(context, Port);
                if (securityError is not null)
                {
                    int status = context.Request.ContentLength is long length
                        && length > McpSecurity.MaxRequestBodyBytes
                        ? StatusCodes.Status413PayloadTooLarge
                        : StatusCodes.Status403Forbidden;
                    await McpSecurity.RejectAsync(context, status, securityError).ConfigureAwait(false);
                    return;
                }
            }
            await next().ConfigureAwait(false);
        });
        app.MapMcp("/mcp");
        return app;
    }

    private void DisposeApp()
    {
        if (_app is null)
        {
            return;
        }
        try
        {
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.Warn($"[警告] 释放 MCP 宿主异常：{ex.Message}");
        }
        finally
        {
            _app = null;
        }
    }
}
