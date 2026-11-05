using System.Collections.Immutable;
using NexusPipeline.Core.Automation;

namespace NexusPipeline.Core.Notifications;

public sealed record BindingRouteInput(Guid BindingId, bool Enabled, ImmutableArray<Guid> TaskIds);
public sealed record QueueRouteInput(Guid QueueId, bool Enabled, ImmutableArray<Guid> TaskIds, ImmutableArray<Guid> BindingIds);

public static class NotificationRoutingPolicy
{
    public static ImmutableArray<NotificationRoute> Select(AutomationQueue? queue, IReadOnlyList<AutomationBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Any(item => item is null)) throw new NotificationTaskException("notification_routes_invalid");
        if (queue is not null && bindings.Any(binding => !queue.ScriptIds.Contains(binding.ScriptId)))
            throw new NotificationTaskException("notification_routes_invalid");
        return SelectFrozen(queue is null ? null : new(queue.Id, queue.NotifyEnabled, queue.NotificationTaskIds, bindings.Select(binding => binding.Id).ToImmutableArray()),
            bindings.Select(item => new BindingRouteInput(item.Id, item.NotifyEnabled, item.NotificationTaskIds)).ToArray());
    }

    public static ImmutableArray<NotificationRoute> SelectFrozen(QueueRouteInput? queue,
        IReadOnlyList<BindingRouteInput> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Any(item => item is null || item.BindingId == Guid.Empty) ||
            bindings.Select(item => item.BindingId).Distinct().Count() != bindings.Count)
            throw new NotificationTaskException("notification_routes_invalid");
        if (queue is not null)
        {
            if (queue.QueueId == Guid.Empty || queue.BindingIds.IsDefault || queue.BindingIds.Length > 4096 ||
                queue.BindingIds.Contains(Guid.Empty) || queue.BindingIds.Distinct().Count() != queue.BindingIds.Length)
                throw new NotificationTaskException("notification_routes_invalid");
            RequireSelection(queue.TaskIds);
            if (bindings.Any(binding => !queue.BindingIds.Contains(binding.BindingId)))
                throw new NotificationTaskException("notification_routes_invalid");
            if (queue.Enabled) return [new(NotificationRoutingKind.Queue, queue.QueueId, new(queue.TaskIds))];
        }
        var routes = ImmutableArray.CreateBuilder<NotificationRoute>();
        foreach (var binding in bindings)
        {
            RequireSelection(binding.TaskIds);
            if (binding.Enabled) routes.Add(new(NotificationRoutingKind.Binding, binding.BindingId, new(binding.TaskIds)));
        }
        return routes.ToImmutable();
    }

    public static void RequireSelection(ImmutableArray<Guid> ids)
    {
        if (ids.IsDefault || ids.Length > 99 || ids.Contains(Guid.Empty) || ids.Distinct().Count() != ids.Length)
            throw new NotificationTaskException("notification_targets_invalid");
    }
}
