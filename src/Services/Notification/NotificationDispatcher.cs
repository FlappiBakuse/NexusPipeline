using NexusPipeline.Extensibility;
using NexusPipeline.Models;
using NexusPipeline.Utilities;

namespace NexusPipeline.Services.Notification;

/// <summary>通知领域服务：只依赖通知 capability provider，不知道具体插件实现。</summary>
internal sealed class NotificationDispatcher
{
    private readonly INotificationChannelProvider _provider;

    public NotificationDispatcher(INotificationChannelProvider provider)
    {
        _provider = provider;
    }

    public async Task NotifyScriptAsync(ScriptInstance script, RunRecord record)
    {
        foreach (INotifyChannel channel in _provider.GetNotificationChannels())
        {
            try
            {
                await channel.NotifyScriptAsync(script, record).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[通知] 通道「{channel.GetType().Name}」发送脚本通知失败：{ex.Message}");
            }
        }
    }

    public async Task NotifyQueueAsync(DispatchQueue queue, List<RunRecord> records)
    {
        foreach (INotifyChannel channel in _provider.GetNotificationChannels())
        {
            try
            {
                await channel.NotifyQueueAsync(queue, records).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[通知] 通道「{channel.GetType().Name}」发送队列通知失败：{ex.Message}");
            }
        }
    }
}
