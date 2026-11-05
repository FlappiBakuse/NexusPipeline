using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Notifications;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class SelectionStorageTests
{
    [Fact(DisplayName = "Actual Catalog Selections Round Trip Sql And Frozen Bundle Without Dropping A Deleted Reference")]
    public async Task PreservesDeletedSelections()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var tasks = new NotificationCatalogService(source.Instance, source.Database, source.Lease,
            fixture.Resources, fixture.Index, new());
        var taskId = Guid.NewGuid();
        _ = await tasks.SaveAsync(Guid.NewGuid(), "local:notification-selection", 0, taskId, 0,
            new("task", "", NotificationTaskKind.Webhook, false, NotificationChannelConfiguration.Default), []);
        var before = source.Definitions.Read(); var binding = before.Bindings.Single(item => item.Id == source.BindingA);
        var queue = before.Queues.Single(item => item.Id == source.QueueId);
        var changes = Empty(before.Revision) with
        {
            Bindings = [binding with { NotifyEnabled = true, NotificationTaskIds = [taskId] }],
            Queues = [queue with { NotifyEnabled = true, NotificationTaskIds = [] }]
        };
        _ = source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "local:notification-selection", changes),
            changes, DateTimeOffset.UtcNow);
        var saved = source.Definitions.Read();
        Assert.Equal(new[] { taskId }, saved.Bindings.Single(item => item.Id == binding.Id).NotificationTaskIds);
        Assert.True(saved.Queues.Single().NotifyEnabled); Assert.Empty(saved.Queues.Single().NotificationTaskIds);
        var codec = new AutomationBundleCodec(new());
        var bundle = codec.Decode(codec.Encode(saved, DateTimeOffset.UtcNow),
            saved.Roots.ToDictionary(item => item.Id, item => item.Path), saved.Revision);
        Assert.Equal(new[] { taskId }, bundle.Definitions.Bindings.Single(item => item.Id == binding.Id).NotificationTaskIds);
        Assert.True(bundle.Definitions.Queues.Single().NotifyEnabled);
        _ = await tasks.DeleteAsync(Guid.NewGuid(), "local:notification-selection", 1, taskId, 1);
        saved = source.Definitions.Read(); binding = saved.Bindings.Single(item => item.Id == binding.Id);
        changes = Empty(saved.Revision) with { Bindings = [binding with { Priority = binding.Priority + 1 }] };
        _ = source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "local:notification-selection", changes),
            changes, DateTimeOffset.UtcNow);
        Assert.Equal(new[] { taskId }, source.Definitions.Read().Bindings.Single(item => item.Id == binding.Id).NotificationTaskIds);
    }

    [Theory(DisplayName = "A New Unknown Selection Consumes Neither Definition Revision Nor Operation")]
    [InlineData("binding")]
    [InlineData("queue")]
    public void RejectsUnknownSelections(string kind)
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        var before = source.Definitions.Read(); var operations = fixture.Count("operations"); var id = Guid.NewGuid();
        var changes = kind == "binding"
            ? Empty(before.Revision) with { Bindings = [before.Bindings[0] with { NotificationTaskIds = [id] }] }
            : Empty(before.Revision) with { Queues = [before.Queues[0] with { NotificationTaskIds = [id] }] };
        var operation = AutomationDefinitionStore.Identity(Guid.NewGuid(), "local:notification-selection", changes);
        Assert.Equal("notification_target_not_found", Assert.Throws<NotificationTaskException>(() =>
            source.Definitions.Commit(operation, changes, DateTimeOffset.UtcNow)).Message);
        Assert.Equal(before.Revision, source.Definitions.Read().Revision); Assert.Equal(operations, fixture.Count("operations"));
    }

    private static AutomationChanges Empty(long revision) => new(revision, [], [], [], [], [], [], []);
}
