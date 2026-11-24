using System.Runtime.Versioning;
using System.Text.Json;
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
        void VerifyExport(AutomationCatalog view)
        {
            using var wire = JsonDocument.Parse(codec.Encode(view, DateTimeOffset.UtcNow));
            var packedBinding = Assert.Single(wire.RootElement.GetProperty("bindings").EnumerateArray(),
                item => item.GetProperty("id").GetGuid() == binding.Id);
            var packedQueue = Assert.Single(wire.RootElement.GetProperty("queues").EnumerateArray());
            Assert.True(packedBinding.GetProperty("notifyEnabled").GetBoolean());
            Assert.Equal(taskId, Assert.Single(packedBinding.GetProperty("notificationTaskIds").EnumerateArray()).GetGuid());
            Assert.Equal(queue.Id, packedQueue.GetProperty("id").GetGuid());
            Assert.True(packedQueue.GetProperty("notifyEnabled").GetBoolean());
            Assert.Empty(packedQueue.GetProperty("notificationTaskIds").EnumerateArray());
        }
        VerifyExport(saved);
        _ = await tasks.DeleteAsync(Guid.NewGuid(), "local:notification-selection", 1, taskId, 1);
        saved = source.Definitions.Read(); binding = saved.Bindings.Single(item => item.Id == binding.Id);
        changes = Empty(saved.Revision) with { Bindings = [binding with { Priority = binding.Priority + 1 }] };
        _ = source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "local:notification-selection", changes),
            changes, DateTimeOffset.UtcNow);
        saved = source.Definitions.Read();
        Assert.Equal(new[] { taskId }, saved.Bindings.Single(item => item.Id == binding.Id).NotificationTaskIds);
        VerifyExport(saved);

        var independent = """
            {"format":"nexus.automation.bundle","version":1,"exportKind":"definitions",
             "bundleId":"0ef65819-c20f-455a-a745-3147ca0fa882","createdAt":"2026-07-02T03:00:00.0000000Z",
             "roots":[{"id":"c039dba3-05f6-4a4c-b50d-69c2355fce49","name":"Selected root","kind":"external"}],
             "scripts":[
               {"id":"b617a37e-326f-4872-81db-09d7e714e6c0","name":"Selection","kind":"generic",
                "rootId":"c039dba3-05f6-4a4c-b50d-69c2355fce49",
                "executable":{"rootId":"c039dba3-05f6-4a4c-b50d-69c2355fce49","relative":"fixture.exe"},
                "arguments":[],"configurationPaths":[],"logPaths":[],"adapterResources":[],"updateConfigurationAtFinish":false,
                "limits":{"maximumAttempts":1,"stallTimeoutSeconds":0,"totalTimeoutSeconds":60},
                "judgement":{"language":"keywords","successKeywords":["fixture"],"failureKeywords":[]}}
             ],
             "users":[{"id":"7f4efc69-c97d-4729-a2d6-6c89d7f38840","name":"Operator","order":1}],
             "bindings":[{"id":"f871a235-344c-422c-8a48-6c90880e6654","scriptId":"b617a37e-326f-4872-81db-09d7e714e6c0",
                "userId":"7f4efc69-c97d-4729-a2d6-6c89d7f38840","enabled":false,"priority":1,
                "notifyEnabled":true,"notificationTaskIds":["b7fb1b68-4b69-47da-b965-ecdfdbf7fa2a"]}],
             "queues":[{"id":"821e4a22-5a1f-4081-8308-a0ab2985fdbf","name":"No selected tasks","order":0,
                "scriptIds":["b617a37e-326f-4872-81db-09d7e714e6c0"],"completionAction":"none",
                "notifyEnabled":true,"notificationTaskIds":[]}],
             "schedules":[],"attachments":[]}
            """u8.ToArray();
        var selection = codec.Decode(independent,
            new Dictionary<Guid, string> { [Guid.Parse("c039dba3-05f6-4a4c-b50d-69c2355fce49")] = saved.Roots[0].Path }, 0).Definitions;
        var decodedBinding = Assert.Single(selection.Bindings);
        Assert.True(decodedBinding.NotifyEnabled);
        Assert.Equal(Guid.Parse("b7fb1b68-4b69-47da-b965-ecdfdbf7fa2a"), Assert.Single(decodedBinding.NotificationTaskIds));
        var decodedQueue = Assert.Single(selection.Queues);
        Assert.True(decodedQueue.NotifyEnabled);
        Assert.Empty(decodedQueue.NotificationTaskIds);
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
