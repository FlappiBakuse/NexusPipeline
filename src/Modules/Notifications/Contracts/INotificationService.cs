using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Notifications;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users;
namespace NexusPipeline.Modules.Notifications.Contracts;


/// <summary>通知应用端口，执行域不感知具体 NotificationDispatcher 实现。</summary>
internal interface INotificationService
{
    Task NotifyScriptAsync(ScriptInstance script, RunRecord record);

    Task NotifyScriptAsync(ScriptInstance script, RunRecord record, UserScriptBinding binding)
    {
        return NotifyScriptAsync(script, record);
    }

    Task NotifyScriptAsync(
        ScriptInstance script,
        RunRecord record,
        UserScriptBinding binding,
        NotificationImage? image)
    {
        return NotifyScriptAsync(script, record, binding);
    }

    Task NotifyQueueAsync(DispatchQueue queue, List<RunRecord> records);
}
