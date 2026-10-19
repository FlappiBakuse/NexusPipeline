using NexusPipeline.Modules.Notifications;
using NexusPipeline.Modules.Queues.Contracts;
using NexusPipeline.Modules.Users.Contracts;

namespace NexusPipeline.Host.Composition.Adapters;

internal sealed class NotificationTargetValidator(NotificationTasks tasks) : IUserNotificationTargets, IQueueNotificationTargets
{
    public string? Validate(IEnumerable<string>? ids, IEnumerable<string>? previous = null) => tasks.ValidateTargets(ids, previous);
}
