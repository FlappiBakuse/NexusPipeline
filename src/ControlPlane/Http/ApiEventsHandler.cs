using System.Net;
using System.Text;
using System.Text.Json;
using NexusPipeline.Modules.Execution.Realtime;
using NexusPipeline.Shared.Serialization;
using NexusPipeline.Platform.Storage;

namespace NexusPipeline.ControlPlane.Http;

[ApiRoute("events", BodyMode = ApiBodyMode.Raw)]
internal static class ApiEventsHandler
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    public static async Task Handle(
        HttpListenerContext context,
        string method,
        CancellationToken cancellationToken,
        RealtimeEventBus bus)
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

            Task<RealtimeEventDelivery?> readTask = subscription.ReadAsync(cancellationToken).AsTask();
            while (!cancellationToken.IsCancellationRequested)
            {
                using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task heartbeatTask = Task.Delay(HeartbeatInterval, heartbeatCancellation.Token);
                Task completed = await Task.WhenAny(readTask, heartbeatTask).ConfigureAwait(false);
                if (completed == heartbeatTask)
                {
                    await WriteCommentAsync(context, "heartbeat", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                heartbeatCancellation.Cancel();
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
                readTask = subscription.ReadAsync(cancellationToken).AsTask();
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
            context.Response.Close();
        }
    }

    private static async Task WriteEventAsync(
        HttpListenerContext context,
        string type,
        RealtimeEventEnvelope envelope,
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
