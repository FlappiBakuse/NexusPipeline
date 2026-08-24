using NexusPipeline.Models;
using NexusPipeline.Utilities;
using NexusPipeline.App.Abstractions;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Services.Notification;

/// <summary>通知领域服务：Webhook / SMTP 是宿主内建能力，插件只能通过显式 DTO 消费它。</summary>
internal sealed class NotificationDispatcher : INotificationService
{
    private readonly ISettingsProvider _settings;

    private readonly TimeSpan _channelTimeout;

    public NotificationDispatcher(ISettingsProvider settings, TimeSpan? channelTimeout = null)
    {
        _settings = settings;
        _channelTimeout = channelTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task NotifyScriptAsync(ScriptInstance script, RunRecord record)
    {
        await NotifyScriptAsync(script, record, null).ConfigureAwait(false);
    }

    public async Task NotifyScriptAsync(ScriptInstance script, RunRecord record, UserScriptBinding? binding)
    {
        AppSettings settings = _settings.Current;
        string? smtpToOverride = string.IsNullOrWhiteSpace(binding?.SmtpTo) ? null : binding!.SmtpTo.Trim();
        if (!HasChannel(settings, smtpToOverride))
        {
            Logger.Info($"[通知] 脚本「{script.Name}」完成，但未配置通知渠道，跳过发送。");
            return;
        }
        Logger.Info($"======== 发送脚本运行状态通知：「{script.Name}」 ========");
        await SendTextAsync(settings, NotificationFormatter.Script(script, record), "脚本", smtpToOverride).ConfigureAwait(false);
    }

    public async Task NotifyQueueAsync(DispatchQueue queue, List<RunRecord> records)
    {
        AppSettings settings = _settings.Current;
        if (!HasChannel(settings))
        {
            Logger.Info($"[通知] 调度队列「{queue.Name}」完成，但未配置通知渠道，跳过发送。");
            return;
        }
        Logger.Info($"======== 发送调度队列汇总通知：「{queue.Name}」 ========");
        await SendTextAsync(settings, NotificationFormatter.Queue(queue, records), "队列", null).ConfigureAwait(false);
    }

    /// <summary>供 Plugin API v1 使用的宿主通知入口；插件不接触 AppSettings 或具体 sender。</summary>
    public async ValueTask SendPluginAsync(PluginNotification notification, CancellationToken cancellationToken)
    {
        if (notification is null)
        {
            throw new ArgumentNullException(nameof(notification));
        }
        AppSettings settings = _settings.Current;
        if (!HasChannel(settings))
        {
            Logger.Info($"[通知] 插件通知「{notification.Title}」未配置通知渠道，跳过发送。");
            return;
        }
        string title = string.IsNullOrWhiteSpace(notification.Title) ? "插件通知" : notification.Title.Trim();
        string body = $"[NexusPipeline] {title}\r\n{notification.Body ?? ""}";
        await SendTextAsync(settings, body, "插件", null).WaitAsync(_channelTimeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendTextAsync(AppSettings settings, string text, string kind, string? smtpToOverride)
    {
        try
        {
            await NotifySender.SendAsync(settings, text, smtpToOverride).WaitAsync(_channelTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Logger.Warn($"[通知] {kind}通知发送超时（{_channelTimeout.TotalSeconds:0.#} 秒）。");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[通知] {kind}通知发送失败：{ex.Message}");
        }
    }

    private static bool HasChannel(AppSettings settings, string? smtpToOverride = null)
    {
        (bool webhookOk, _) = WebhookSender.Status(settings);
        (bool smtpOk, _) = SmtpSender.Status(settings, smtpToOverride);
        return webhookOk || smtpOk;
    }
}
