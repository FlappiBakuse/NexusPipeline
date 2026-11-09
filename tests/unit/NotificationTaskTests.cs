using System.Collections.Immutable;
using NexusPipeline.Core.Notifications;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class NotificationTaskTests
{
    [Fact(DisplayName = "Lowering The Notification Limit Keeps Every Existing Task Editable And Deletable")]
    public void KeepsExistingTasks()
    {
        var current = NotificationTaskPolicy.SetLimit(NotificationTaskCatalog.Empty, 0, 99);
        for (var index = 0; index < 11; index++) current = Add(current, "任务" + index);
        var reduced = NotificationTaskPolicy.SetLimit(current, current.Revision, 10);
        var first = reduced.Tasks[0];
        var changed = NotificationTaskPolicy.Save(reduced, reduced.Revision, first.Id, first.Revision, Input("修改"));
        Assert.Equal(11, changed.Tasks.Length);
        Assert.Equal("修改", changed.Tasks[0].Name);
        Assert.Equal("notification_limit_reached", Assert.Throws<NotificationTaskException>(() => Add(changed, "新增")).Message);
        var removed = NotificationTaskPolicy.Remove(changed, changed.Revision, first.Id, changed.Tasks[0].Revision);
        Assert.Equal(10, removed.Tasks.Length);
    }

    [Theory(DisplayName = "Notification Limits Outside One Through Ninety Nine Cannot Change The Catalog")]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(-1)]
    public void ValidatesTaskLimits(int maximum)
    {
        Assert.Throws<NotificationTaskException>(() => NotificationTaskPolicy.SetLimit(NotificationTaskCatalog.Empty, 0, maximum));
        Assert.Equal(10, NotificationTaskCatalog.Empty.MaximumTasks);
    }

    [Theory(DisplayName = "Invalid Notification Names And Remarks Do Not Publish A New Task")]
    [InlineData(" ", "")]
    [InlineData("abcdefghijklmnopqrstuvwxyabcdefghijklmnopqrstuvwxyabcdefghijklmno", "")]
    [InlineData("任务", "nul\0remark")]
    public void RejectsInvalidText(string name, string remark)
    {
        Assert.Throws<NotificationTaskException>(() => NotificationTaskPolicy.Save(NotificationTaskCatalog.Empty, 0,
            Guid.NewGuid(), 0, Input(name) with { Remark = remark }));
    }

    [Fact(DisplayName = "Notification Name And Remark Limits Use Utf 8 Bytes And Names Remain Unique Ignoring Case")]
    public void BoundsUniqueNames()
    {
        var catalog = Add(NotificationTaskCatalog.Empty, new string('中', 21));
        Assert.Throws<NotificationTaskException>(() => Add(catalog, new string('中', 22)));
        catalog = Add(catalog, "First");
        Assert.Throws<NotificationTaskException>(() => Add(catalog, " first "));
        var id = Guid.NewGuid();
        var accepted = NotificationTaskPolicy.Save(catalog, catalog.Revision, id, 0, Input("备注") with { Remark = new string('中', 170) });
        Assert.Equal(510, System.Text.Encoding.UTF8.GetByteCount(accepted.Tasks[^1].Remark));
        Assert.Throws<NotificationTaskException>(() => NotificationTaskPolicy.Save(catalog, catalog.Revision,
            Guid.NewGuid(), 0, Input("超长备注") with { Remark = new string('中', 171) }));
    }

    [Fact(DisplayName = "Notification Reordering Requires The Whole Unique Set And Preserves Each Task Revision")]
    public void RequiresCompleteOrder()
    {
        var catalog = Add(Add(NotificationTaskCatalog.Empty, "第一项"), "第二项");
        var reversed = catalog.Tasks.Select(task => task.Id).Reverse().ToImmutableArray();
        var reordered = NotificationTaskPolicy.Reorder(catalog, catalog.Revision, reversed);
        Assert.Equal(reversed, reordered.Tasks.Select(task => task.Id));
        Assert.Equal(catalog.Tasks.Reverse(), reordered.Tasks);
        Assert.Equal(catalog.Revision + 1, reordered.Revision);
        Assert.Throws<NotificationTaskException>(() => NotificationTaskPolicy.Reorder(catalog, catalog.Revision, [catalog.Tasks[0].Id]));
        Assert.Throws<NotificationTaskException>(() => NotificationTaskPolicy.Reorder(catalog, catalog.Revision,
            [catalog.Tasks[0].Id, catalog.Tasks[0].Id]));
        Assert.Throws<NotificationTaskException>(() => NotificationTaskPolicy.Reorder(catalog, catalog.Revision,
            [catalog.Tasks[0].Id, Guid.NewGuid()]));
    }

    [Fact(DisplayName = "Notification Task Kind And Original Revisions Cannot Be Replaced By A Stale Writer")]
    public void RequiresOriginalRevisions()
    {
        var catalog = Add(NotificationTaskCatalog.Empty, "原任务"); var task = catalog.Tasks[0];
        Assert.Throws<NotificationTaskException>(() => NotificationTaskPolicy.Save(catalog, 0, task.Id, task.Revision, Input("改名")));
        Assert.Throws<NotificationTaskException>(() => NotificationTaskPolicy.Save(catalog, catalog.Revision, task.Id, 0, Input("改名")));
        Assert.Equal("notification_type_immutable", Assert.Throws<NotificationTaskException>(() => NotificationTaskPolicy.Save(
            catalog, catalog.Revision, task.Id, task.Revision, Input("改名") with { Kind = NotificationTaskKind.Webhook })).Message);
    }

    [Fact(DisplayName = "Deleted Notification References Can Be Kept But New Unknown Selections Are Rejected")]
    public void KeepsDeletedReferences()
    {
        var catalog = Add(NotificationTaskCatalog.Empty, "原任务"); var task = catalog.Tasks[0];
        var selection = new NotificationTaskSelection([task.Id]);
        var removed = NotificationTaskPolicy.Remove(catalog, catalog.Revision, task.Id, task.Revision);
        Assert.Equal(selection.TaskIds.ToArray(), NotificationTaskPolicy.ValidateSelection(removed, selection, selection).TaskIds.ToArray());
        Assert.Throws<NotificationTaskException>(() => NotificationTaskPolicy.ValidateSelection(removed, selection));
        Assert.Throws<NotificationTaskException>(() => NotificationTaskPolicy.ValidateSelection(removed,
            new([task.Id, Guid.NewGuid()]), selection));
        Assert.Empty(NotificationTaskPolicy.ValidateSelection(removed, new([]), selection).TaskIds);
        Assert.Throws<NotificationTaskException>(() => NotificationTaskPolicy.ValidateSelection(catalog, new([task.Id, task.Id])));
    }

    [Theory(DisplayName = "Invalid Notification Timeouts And Smtp Ports Never Become Saved Definitions")]
    [InlineData(0, 465)]
    [InlineData(301, 465)]
    [InlineData(30, 0)]
    [InlineData(30, 65536)]
    public void RejectsInvalidEndpoints(int seconds, int port)
    {
        Assert.Throws<NotificationTaskException>(() => NotificationTaskPolicy.Save(NotificationTaskCatalog.Empty, 0, Guid.NewGuid(),
            0, Input("任务") with { Configuration = NotificationChannelConfiguration.Default with { TimeoutSeconds = seconds, SmtpPort = port } }));
    }

    private static NotificationTaskInput Input(string name) => new(name, "", NotificationTaskKind.Smtp, false,
        NotificationChannelConfiguration.Default);
    private static NotificationTaskCatalog Add(NotificationTaskCatalog current, string name) =>
        NotificationTaskPolicy.Save(current, current.Revision, Guid.NewGuid(), 0, Input(name));
}
