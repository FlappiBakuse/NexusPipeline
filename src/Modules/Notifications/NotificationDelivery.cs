using System.Text.Json;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Shared.Results;

namespace NexusPipeline.Modules.Notifications;

internal sealed class NotificationDelivery
{
    private readonly NotificationTasks _tasks;
    private readonly Func<NotificationTask, string, NotificationImage?, CancellationToken, Task<bool>> _send;

    public NotificationDelivery(NotificationTasks tasks, OutboundHttpClientProvider? outbound = null,
        Func<NotificationTask, string, NotificationImage?, CancellationToken, Task<bool>>? send = null)
    {
        _tasks = tasks;
        _send = send ?? ((task, text, image, token) => task.Type == "smtp"
            ? SmtpSender.SendAsync(task.Configuration, text, image, cancellationToken: token)
            : WebhookSender.SendAsync(task.Configuration, text, outbound, image, token));
    }

    public async Task SendAsync(IEnumerable<string> ids, string text, NotificationImage? image = null,
        CancellationToken cancellationToken = default)
    {
        var selected = ids.Distinct(StringComparer.Ordinal).ToArray();
        var snapshot = _tasks.Snapshot(selected);
        foreach (string missing in selected.Except(snapshot.Select(task => task.Id), StringComparer.Ordinal))
            Logger.Warn("[通知] 所选通知任务不可用，已跳过。");
        await Parallel.ForEachAsync(snapshot, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (task, token) =>
            {
                if (!task.Enabled || NotificationTasks.ValidateConfiguration(task) is not null)
                {
                    Logger.Info($"[通知] 任务 {Describe(task)}未启用或配置不可用，已跳过。");
                    return;
                }
                await SendOneAsync(task, text, image, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    public async Task<OperationResult<bool>> TestAsync(string id, CancellationToken cancellationToken = default)
    {
        if (_tasks.BlockedReason is { } blocked)
            return OperationResult.Failure(blocked, blocked, OperationErrorKind.Unavailable);
        NotificationTask? task = _tasks.Snapshot([id]).SingleOrDefault();
        if (task is null) return OperationResult.Failure("notification_not_found", "通知任务不存在", OperationErrorKind.NotFound);
        if (NotificationTasks.ValidateConfiguration(task) is { } error)
            return OperationResult.Failure(error, error, OperationErrorKind.Validation);
        bool ok = await SendOneAsync(task, "[NexusPipeline] 通知测试 / Notification test", null, cancellationToken).ConfigureAwait(false);
        return ok ? OperationResult.Ok() : OperationResult.Failure("notification_test_failed", "通知测试失败", OperationErrorKind.Unavailable);
    }

    private static string Describe(NotificationTask task) => $"{task.Id} {JsonSerializer.Serialize(task.Name)}（{task.Type}）";

    private async Task<bool> SendOneAsync(NotificationTask task, string text, NotificationImage? image, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(task.Configuration.Timeout));
        try
        {
            bool ok = await _send(task, text, task.Configuration.ScreenshotEnabled ? image : null, timeout.Token).ConfigureAwait(false);
            if (!ok) Logger.Warn($"[通知] 任务 {Describe(task)}发送失败。");
            return ok;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            Logger.Warn($"[通知] 任务 {Describe(task)}发送超时。");
            return false;
        }
        catch
        {
            Logger.Warn($"[通知] 任务 {Describe(task)}发送失败。");
            return false;
        }
    }
}
