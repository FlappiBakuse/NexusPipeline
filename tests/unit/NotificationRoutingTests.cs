using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Notifications;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class NotificationRoutingTests
{
    [Fact(DisplayName = "An Enabled Queue With No Selected Tasks Does Not Fall Back To Binding Notifications")]
    public void HonorsEmptyQueue()
    {
        var binding = Binding() with { NotificationTaskIds = [Guid.NewGuid()] };
        var queue = Queue(binding) with { NotifyEnabled = true, NotificationTaskIds = [] };
        var route = Assert.Single(NotificationRoutingPolicy.Select(queue, [binding]));
        Assert.Equal(NotificationRoutingKind.Queue, route.Kind); Assert.Equal(queue.Id, route.SourceId);
        Assert.Empty(route.Selection.TaskIds);
    }

    [Fact(DisplayName = "A Disabled Queue Uses Each Binding Selection Without Merging Two Sources")]
    public void PreservesBindingRoutes()
    {
        var task = Guid.NewGuid();
        var first = Binding() with { NotificationTaskIds = [task] };
        var second = Binding() with { NotificationTaskIds = [task] };
        var queue = Queue(first, second);
        var routes = NotificationRoutingPolicy.Select(queue, [first, second]);
        Assert.Equal(new[] { first.Id, second.Id }, routes.Select(route => route.SourceId));
        Assert.All(routes, route => Assert.Equal(new[] { task }, route.Selection.TaskIds));
    }

    [Fact(DisplayName = "Existing Deleted References Remain Visible But Cannot Be Added To A New Selection")]
    public void PreservesDeletedReferences()
    {
        var deleted = Guid.NewGuid(); var known = new HashSet<Guid>();
        var selection = new NotificationTaskSelection([deleted]);
        Assert.Equal(new[] { deleted }, NotificationTaskPolicy.ValidateSelectionIds(known, selection, selection).TaskIds);
        Assert.Equal("notification_target_not_found", Assert.Throws<NotificationTaskException>(() =>
            NotificationTaskPolicy.ValidateSelectionIds(known, selection)).Message);
    }

    [Theory(DisplayName = "Malformed Selections Cannot Enter Frozen Routing")]
    [InlineData("empty-id")]
    [InlineData("duplicate")]
    [InlineData("default-array")]
    public void RejectsMalformedSelections(string fault)
    {
        var task = Guid.NewGuid();
        var ids = fault switch { "empty-id" => ImmutableArray.Create(Guid.Empty),
            "duplicate" => ImmutableArray.Create(task, task), _ => default };
        var binding = Binding() with { NotificationTaskIds = ids };
        Assert.Throws<NotificationTaskException>(() => NotificationRoutingPolicy.Select(null, [binding]));
    }

    [Fact(DisplayName = "A Disabled Binding Does Not Dispatch Its Selected Notifications")]
    public void SkipsDisabledBindings()
    {
        var task = Guid.NewGuid(); var binding = Binding() with { NotifyEnabled = false, NotificationTaskIds = [task] };
        Assert.Empty(NotificationRoutingPolicy.Select(null, [binding]));
    }

    [Fact(DisplayName = "Binding And Queue Defaults Match Their Distinct Notification Policies")]
    public void KeepsDistinctDefaults()
    {
        var binding = Binding(); var queue = Queue(binding);
        Assert.True(binding.NotifyEnabled); Assert.False(queue.NotifyEnabled);
        Assert.Empty(binding.NotificationTaskIds); Assert.Empty(queue.NotificationTaskIds);
    }

    private static AutomationBinding Binding() => new(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), true, 0);
    private static AutomationQueue Queue(params AutomationBinding[] bindings) =>
        new(Guid.NewGuid(), 1, "queue", bindings.Select(binding => binding.ScriptId).Distinct().ToImmutableArray(), AutomationCompletionAction.None);
}
