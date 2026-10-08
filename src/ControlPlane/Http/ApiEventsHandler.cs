using System.Net;
using System.Text;
using System.Text.Json;
using NexusPipeline.Modules.Execution.Realtime;
using NexusPipeline.Shared.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.ControlPlane.Http.Services;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.ControlPlane.Http;

[ApiRoute("events", BodyMode = ApiBodyMode.Raw)]
internal static class ApiEventsHandler
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    public static async Task Handle(
        HttpListenerContext context,
        string method,
        CancellationToken cancellationToken,
        RealtimeEventBus bus,
        IManagementPageRefreshPort managementRefresh)
    {
        if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            await HttpHelper.MethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-cache, no-store, no-transform";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        using RealtimeEventSubscription subscription = bus.Subscribe();
        using ManagementPageRefreshSubscription? management = RequestAccessPolicy.Connection(context.Request) == PluginClientConnectionKind.Local
            ? managementRefresh.Subscribe() : null;
        using var reads = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await WriteEventAsync(
                context,
                RealtimeEventNames.StreamReady,
                bus.CreateEnvelope(RealtimeEventNames.StreamReady, new
                {
                    serverTime = DateTimeOffset.UtcNow,
                }),
                cancellationToken).ConfigureAwait(false);

            Task<RealtimeEventDelivery?> readTask = subscription.ReadAsync(reads.Token).AsTask();
            Task<ManagementPageRefreshRequest>? managementRead = management?.ReadAsync(reads.Token).AsTask();
            while (!cancellationToken.IsCancellationRequested)
            {
                using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task heartbeatTask = Task.Delay(HeartbeatInterval, heartbeatCancellation.Token);
                Task completed = await Task.WhenAny(managementRead is null ? [readTask, heartbeatTask] : [readTask, managementRead, heartbeatTask]).ConfigureAwait(false);
                if (completed == heartbeatTask)
                {
                    await WriteCommentAsync(context, "heartbeat", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                heartbeatCancellation.Cancel();
                if (completed == managementRead)
                {
                    ManagementPageRefreshRequest request = await managementRead!.ConfigureAwait(false);
                    if (request.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                        await WriteEventAsync(context, "management.page-refresh", new { data = new { request.RequestId, request.ExpiresAt } }, cancellationToken).ConfigureAwait(false);
                    managementRead = management!.ReadAsync(reads.Token).AsTask();
                    continue;
                }
                RealtimeEventDelivery? delivery = await readTask.ConfigureAwait(false);
                if (delivery is null)
                {
                    return;
                }
                if (delivery.Value.Missed)
                {
                    await WriteEventAsync(
                        context,
                        RealtimeEventNames.StreamMissed,
                        bus.CreateEnvelope(RealtimeEventNames.StreamMissed, new
                        {
                            reason = "subscriber_queue_overflow",
                        }),
                        cancellationToken).ConfigureAwait(false);
                }
                else if (delivery.Value.Event is RealtimeEvent item)
                {
                    await WriteEventAsync(context, item.Type, item.Envelope, cancellationToken).ConfigureAwait(false);
                }
                readTask = subscription.ReadAsync(reads.Token).AsTask();
            }
        }
        catch (IOException)
        {
            // 客户端断开连接时属于正常收尾路径。
        }
        catch (ObjectDisposedException)
        {
            // 服务停止或客户端关闭连接时属于正常收尾路径。
        }
        catch (HttpListenerException)
        {
            // HttpListener 在连接被关闭后可能以此异常报告写失败。
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // WebServer 停止时取消长连接。
        }
        finally
        {
            reads.Cancel();
            context.Response.Close();
        }
    }

    private static async Task WriteEventAsync(
        HttpListenerContext context,
        string type,
        object envelope,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(envelope, JsonOpts.Web);
        string frame = $"event: {type}\n" + $"data: {json}\n\n";
        byte[] bytes = Encoding.UTF8.GetBytes(frame);
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await context.Response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteCommentAsync(
        HttpListenerContext context,
        string comment,
        CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($": {comment}\n\n");
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await context.Response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
