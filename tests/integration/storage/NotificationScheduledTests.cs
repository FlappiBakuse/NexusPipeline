using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class NotificationScheduledTests
{
    [Theory(DisplayName = "The Actual Minute Tick Dispatches Saved Revisions And Publishes Only Its Bound Saved Notification Targets")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchesSavedRevisions(bool savedTask)
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        await using var fixture = new NotificationScheduledFixture();
        if (savedTask) await fixture.SaveNotification(sink.Address);
        await fixture.Activate(); await fixture.Tick.TickAsync(); await fixture.PumpUntilResult();
        var state = Assert.Single(fixture.States());
        var submissionFailure = fixture.SubmissionFailure.Task.IsCompleted ? await fixture.SubmissionFailure.Task : null;
        Assert.Null(submissionFailure is null ? null : submissionFailure.GetType().Name + " " + submissionFailure.StackTrace);
        Assert.Equal("returned", state.Result!.Completion); Assert.Equal(BusinessOutcome.NotApplicable, state.Result.Returned!.Business);
        Assert.Equal(3, state.Source.Document.PlansRevision); Assert.Equal(2, state.Source.Document.Task.Revision);
        Assert.Equal(1, fixture.Effects); Assert.Equal(1, fixture.PlannedCalls); Assert.Equal(0, fixture.ComponentCalls);
        Assert.Equal(1, fixture.Base.Count("notification_scheduled_sources"));
        Assert.Equal(savedTask ? 1 : 0, fixture.Base.Count("notification_deliveries"));
        await fixture.Notifications.RunPassAsync();
        Assert.Equal(savedTask ? 1 : 0, sink.Requests.Count);
        Assert.False(fixture.Notifications.HasPendingActivity);
        await fixture.Tick.TickAsync(); await fixture.Notifications.RunPassAsync();
        Assert.Equal(1, fixture.Effects); Assert.Equal(1, fixture.Base.Count("scheduled_provider_occurrences"));
        Assert.Equal(savedTask ? 1 : 0, sink.Requests.Count);
        Assert.Equal(0, fixture.Base.Count("attempt_receipts")); Assert.Equal(0, fixture.Base.Count("run_records"));
        DeliveryReferenceIndex.VerifyReadOnly(fixture.Base.Source.Instance, fixture.Base.Source.Database, new());
    }

    [Fact(DisplayName = "A Closed Scheduling Source Restores Its Own Notification Queue And Rejects A Different Run")]
    public async Task RestoresScheduledQueue()
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        await using var fixture = new NotificationScheduledFixture();
        await fixture.SaveNotification(sink.Address);
        await fixture.Activate(); await fixture.Tick.TickAsync(); await fixture.PumpUntilResult();
        var invocation = Assert.Single(fixture.States()).Source.Document.Invocation;
        var source = fixture.Base.Source;
        BlobReference reference;
        using (var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.All))
        using (var transaction = connection.BeginTransaction(deferred: true))
            reference = Assert.IsType<BlobReference>(NotificationScheduledRows.Lookup(connection, transaction, invocation.NotificationOperationId));
        Assert.Throws<IOException>(() => NotificationScheduledSource.VerifyCommitted(source.Instance, source.Database, new(), reference, Guid.NewGuid()));
        await fixture.Notifications.DisposeAsync();

        var secrets = new SecretFieldService(source.Instance, source.Database, source.Lease, fixture.Base.Resources, fixture.Base.Index, new());
        await using var restarted = new NotificationRuntime(source.Instance, source.Database, new(), source.Lease,
            fixture.Base.Resources, fixture.Base.Index, secrets);
        restarted.InitializeStartup();
        Assert.True(restarted.HasPendingActivity);
        await restarted.RunPassAsync(); await restarted.RunPassAsync();
        Assert.Single(sink.Requests);
        Assert.False(restarted.HasPendingActivity);
    }

    [Fact(DisplayName = "A Task Revision Changed After Read Plans Cannot Execute Its Saved Effect Or Publish Notifications")]
    public async Task RejectsChangedTasks()
    {
        await using var fixture = new NotificationScheduledFixture();
        fixture.AfterPlanSnapshot = () => { fixture.ChangePrivateTask(); return Task.CompletedTask; };
        await fixture.Activate(); await fixture.Tick.TickAsync(); await fixture.PumpUntilResult();
        var state = Assert.Single(fixture.States());
        Assert.Equal("unknown", state.Result!.Completion);
        Assert.Equal(3, state.Source.Document.PlansRevision); Assert.Equal(2, state.Source.Document.Task.Revision);
        Assert.Equal(0, fixture.Effects); Assert.Equal(1, fixture.PlannedCalls); Assert.Equal(0, fixture.ComponentCalls);
        Assert.Equal(0, fixture.Base.Count("notification_scheduled_sources")); Assert.Equal(0, fixture.Base.Count("notification_deliveries"));
        Assert.Equal(0, fixture.Base.Count("scheduled_provider_pending"));
        await fixture.Tick.TickAsync(); Assert.Equal(1, fixture.PlannedCalls);
    }

    [Fact(DisplayName = "Plans Returned In Another Minute Cannot Create A Missed Occurrence")]
    public async Task RejectsLatePlans()
    {
        await using var fixture = new NotificationScheduledFixture();
        fixture.AfterPlanSnapshot = () => { fixture.Clock.Move(TimeSpan.FromMinutes(1)); return Task.CompletedTask; };
        await fixture.Activate(); await fixture.Tick.TickAsync();
        fixture.AfterPlanSnapshot = null;
        await fixture.Tick.TickAsync();
        Assert.Empty(fixture.States()); Assert.Equal(0, fixture.Effects); Assert.Equal(0, fixture.PlannedCalls);
        Assert.Equal(0, fixture.Base.Count("notification_scheduled_sources")); Assert.Equal(0, fixture.Base.Count("notification_deliveries"));
    }

    [Fact(DisplayName = "An Actually Ended Unknown Callback Closes With Its Real Regressed Utc And Cannot Replay")]
    public async Task KeepsUnknownCompletion()
    {
        await using var fixture = new NotificationScheduledFixture {
            HoldCallback = true, ThrowAfterCallbackGate = true, SubmitNotification = false };
        await fixture.Activate(); await fixture.Tick.TickAsync();
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var before = Assert.Single(fixture.States()); Assert.NotNull(before.Begin); Assert.Null(before.Result);
        fixture.Clock.Move(TimeSpan.FromMinutes(-1)); fixture.CallbackGate.TrySetResult();
        await fixture.PumpUntilResult();
        var state = Assert.Single(fixture.States());
        Assert.Equal("unknown", state.Result!.Completion); Assert.True(state.Result.WallClockRegressed);
        Assert.Equal(fixture.Clock.GetUtcNow(), state.Result.ObservedAtUtc);
        Assert.True(state.Result.ObservedAtUtc < state.Begin!.ObservedAtUtc);
        Assert.NotNull(state.Result.FinalConsumedMilliseconds);
        Assert.True(state.Result.FinalConsumedMilliseconds >= state.Begin.ConsumedMilliseconds);
        Assert.Equal(0, fixture.Base.Count("scheduled_provider_pending"));
        await fixture.Tick.TickAsync(); Assert.Equal(1, fixture.PlannedCalls); Assert.Equal(1, fixture.Effects);
        Assert.NotNull(fixture.Base.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
        Assert.Equal(0, fixture.Base.Count("attempt_receipts")); Assert.Equal(0, fixture.Base.Count("notification_deliveries"));
    }

    [Fact(DisplayName = "A Source Fanout Sql Failure Rolls Back Every Membership And Can Resubmit The Same Live Invocation Exactly Once")]
    public async Task RevertsFailedFanout()
    {
        await using var sink = new NotificationHttpSink((_, _) => Task.FromResult(new NotificationSinkReply(200, "")));
        await using var fixture = new NotificationScheduledFixture { RetrySubmissionAfterSqlFailure = true };
        await fixture.SaveNotification(sink.Address); await fixture.Activate();
        fixture.Base.Execute("CREATE TRIGGER notifications.fail_scheduled_notification BEFORE INSERT ON notification_scheduled_deliveries BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        try
        {
            await fixture.Tick.TickAsync();
            Assert.IsType<SqliteException>(await fixture.SubmissionFailure.Task.WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Equal(0, fixture.Base.Count("notification_scheduled_sources"));
            Assert.Equal(0, fixture.Base.Count("notification_scheduled_deliveries"));
            Assert.Equal(0, fixture.Base.Count("notification_deliveries"));
            Assert.Equal(0, fixture.Base.Count("notification_delivery_secrets")); Assert.Empty(sink.Requests);
        }
        finally { fixture.Base.Execute("DROP TRIGGER notifications.fail_scheduled_notification;"); fixture.RetrySubmission.TrySetResult(); }
        await fixture.PumpUntilResult();
        Assert.Equal("returned", Assert.Single(fixture.States()).Result!.Completion);
        Assert.Equal(1, fixture.Effects); Assert.Equal(1, fixture.Base.Count("notification_scheduled_sources"));
        Assert.Equal(1, fixture.Base.Count("notification_scheduled_deliveries")); Assert.Equal(1, fixture.Base.Count("notification_deliveries"));
        await fixture.Notifications.RunPassAsync(); await fixture.Notifications.RunPassAsync();
        Assert.Single(sink.Requests); Assert.False(fixture.Notifications.HasPendingActivity);
        DeliveryReferenceIndex.VerifyReadOnly(fixture.Base.Source.Instance, fixture.Base.Source.Database, new());
    }
}
