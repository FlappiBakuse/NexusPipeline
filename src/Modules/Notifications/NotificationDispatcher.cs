using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Notifications.Contracts;
using NexusPipeline.Modules.Queues;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Users;

namespace NexusPipeline.Modules.Notifications;

internal sealed class NotificationDispatcher(NotificationDelivery delivery, NotificationTasks tasks) : INotificationService
{
    public Task NotifyScriptAsync(ScriptInstance script, RunRecord record) => Task.CompletedTask;
    public Task NotifyScriptAsync(ScriptInstance script, RunRecord record, UserScriptBinding? binding) =>
        NotifyScriptAsync(script, record, binding, null);
    public Task NotifyScriptAsync(ScriptInstance script, RunRecord record, UserScriptBinding? binding, NotificationImage? image) =>
        delivery.SendAsync(binding?.NotificationTaskIds ?? [], NotificationFormatter.Script(script, record), image);
    public Task NotifyQueueAsync(DispatchQueue queue, List<RunRecord> records) =>
        delivery.SendAsync(queue.NotificationTaskIds, NotificationFormatter.Queue(queue, records));
    public ValueTask SendPluginAsync(PluginNotification notification, CancellationToken cancellationToken) =>
        new(delivery.SendAsync(notification.NotificationTaskIds, $"[NexusPipeline] {notification.Title}\r\n{notification.Body}", cancellationToken: cancellationToken));
    public ValueTask<IReadOnlyList<PluginNotificationTask>> ListAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<PluginNotificationTask>>(tasks.List()
            .Select(task => new PluginNotificationTask(task.Id, task.Name, task.Type, task.Enabled, task.Configured)).ToArray());
    }
    public string? ValidateTargets(IReadOnlyList<string> ids, IReadOnlyList<string>? previous) => tasks.ValidateTargets(ids, previous);
}
