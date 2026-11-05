using System.Runtime.Versioning;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Notifications;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ControlAdmissionTests
{
    [Theory(DisplayName = "The Public Saved Test Entry Registers The Actual Queue And Delivers Exactly Once Without A Caller Root")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliversAcceptedQueue(bool enabled)
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        await using var fixture = new SavedTaskFixture();
        await fixture.Save(new(sink.Address, "public"), enabled);
        var source = fixture.Base.Source;
        await using var runtime = new NotificationRuntime(source.Instance, source.Database, new(), source.Lease,
            fixture.Base.Resources, fixture.Base.Index,
            new(source.Instance, source.Database, source.Lease, fixture.Base.Resources, fixture.Base.Index, new()));
        runtime.InitializeStartup();
        var operation = Guid.NewGuid();
        var first = runtime.EnqueueTaskTest(operation, "local:control-test", 1, fixture.TaskId, 1);
        var replay = runtime.EnqueueTaskTest(operation, "local:control-test", 1, fixture.TaskId, 1);
        Assert.True(first.IsNew); Assert.False(replay.IsNew);
        Assert.Equal(operation, replay.OperationId); Assert.Equal(fixture.TaskId, replay.TaskId);
        Assert.True(runtime.HasPendingActivity); Assert.Empty(sink.Requests);
        await runtime.RunPassAsync();
        Assert.Equal("/public", Assert.Single(sink.Requests).Target);
        await runtime.RunPassAsync();
        Assert.Single(sink.Requests); Assert.False(runtime.HasPendingActivity);
        Assert.Equal(0, fixture.Base.Count("attempt_receipts")); Assert.Equal(0, fixture.Base.Count("run_records"));
        Assert.False(runtime.EnqueueTaskTest(operation, "local:control-test", 1, fixture.TaskId, 1).IsNew);
        Assert.Single(sink.Requests);
    }

    [Fact(DisplayName = "The Public Saved Test Entry Rejects Another Subject Or Task For The Same Durable Operation")]
    public async Task BindsSavedOperation()
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        await using var fixture = new SavedTaskFixture();
        await fixture.Save(sink.Address);
        var source = fixture.Base.Source;
        await using var runtime = new NotificationRuntime(source.Instance, source.Database, new(), source.Lease,
            fixture.Base.Resources, fixture.Base.Index,
            new(source.Instance, source.Database, source.Lease, fixture.Base.Resources, fixture.Base.Index, new()));
        runtime.InitializeStartup();
        var operation = Guid.NewGuid();
        _ = runtime.EnqueueTaskTest(operation, "local:control-test", 1, fixture.TaskId, 1);
        var rows = fixture.Base.Count("operations");
        Assert.Throws<OperationConflictException>(() =>
            runtime.EnqueueTaskTest(operation, "remote:other", 1, fixture.TaskId, 1));
        Assert.Throws<OperationConflictException>(() =>
            runtime.EnqueueTaskTest(operation, "local:control-test", 1, Guid.NewGuid(), 1));
        Assert.Equal(rows, fixture.Base.Count("operations"));
        Assert.Equal(1, fixture.Base.Count("notification_deliveries")); Assert.Empty(sink.Requests);
    }
}
