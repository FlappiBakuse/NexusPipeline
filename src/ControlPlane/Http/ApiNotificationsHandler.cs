using System.Net;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Notifications;
using NexusPipeline.Shared.Results;

namespace NexusPipeline.ControlPlane.Http;

[ApiRoute("notifications")]
internal static class ApiNotificationsHandler
{
    public static async Task Handle(HttpListenerContext context, string method, string[] seg, string body,
        NotificationTasks tasks, NotificationDelivery delivery)
    {
        if (method == "GET" && seg.Length == 1)
        {
            if (tasks.BlockedReason is { } reason) { await HttpHelper.ErrorAsync(context, reason, 503); return; }
            await HttpHelper.WriteJsonAsync(context, tasks.List()); return;
        }
        if (method == "GET" && seg.Length == 2)
        {
            var task = tasks.Find(seg[1]);
            if (task is null) await HttpHelper.NotFoundAsync(context);
            else await HttpHelper.WriteJsonAsync(context, task);
            return;
        }
        if (method == "PUT" && seg.Length == 2 && seg[1] == "order")
        {
            JsonNode? node = HttpHelper.ParseBody(body);
            var ids = node?["ids"] is JsonArray array ? array.Select(item => item?.ToString() ?? "").ToArray() : null;
            await Write(context, tasks.Reorder(ids)); return;
        }
        if (method == "POST" && seg.Length == 1 || method == "PUT" && seg.Length == 2)
        {
            var input = HttpHelper.ParseBody<NotificationTaskInput>(body);
            if (input is null) { await HttpHelper.ErrorAsync(context, "notification_fields_invalid", 400); return; }
            await Write(context, tasks.Save(method == "POST" ? null : seg[1], input)); return;
        }
        if (method == "DELETE" && seg.Length == 2) { await Write(context, tasks.Delete(seg[1])); return; }
        if (method == "POST" && seg.Length == 3 && seg[2] == "test")
        {
            await Write(context, await delivery.TestAsync(seg[1])); return;
        }
        await HttpHelper.MethodNotAllowedAsync(context);
    }

    private static Task Write<T>(HttpListenerContext context, OperationResult<T> result) => result.Succeeded
        ? HttpHelper.WriteJsonAsync(context, result.Value!)
        : ApplicationErrorResponse.WriteAsync(context, result.Error!);
}
