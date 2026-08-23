using NexusPipeline.Extensibility;
using NexusPipeline.Models;
using NexusPipeline.Utilities;
using NexusPipeline.App.Abstractions;

namespace NexusPipeline.Services.Notification;

/// <summary>通知领域服务：只依赖通知 capability provider，不知道具体插件实现。</summary>
internal sealed class NotificationDispatcher : INotificationService
{
    private readonly INotificationChannelProvider _provider;

    private readonly TimeSpan _channelTimeout;

    public NotificationDispatcher(INotificationChannelProvider provider, TimeSpan? channelTimeout = null)
    {
        _provider = provider;
        _channelTimeout = channelTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task NotifyScriptAsync(ScriptInstance script, RunRecord record)
    {
        foreach (INotifyChannel channel in _provider.GetNotificationChannels())
        {
            try
            {
                await channel.NotifyScriptAsync(script, record).WaitAsync(_channelTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Logger.Warn($"[通知] 通道「{channel.GetType().Name}」发送脚本通知超时（{_channelTimeout.TotalSeconds:0.#} 秒），已继续后续通道。");
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
                await channel.NotifyQueueAsync(queue, records).WaitAsync(_channelTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Logger.Warn($"[通知] 通道「{channel.GetType().Name}」发送队列通知超时（{_channelTimeout.TotalSeconds:0.#} 秒），已继续后续通道。");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[通知] 通道「{channel.GetType().Name}」发送队列通知失败：{ex.Message}");
            }
        }
    }
}
