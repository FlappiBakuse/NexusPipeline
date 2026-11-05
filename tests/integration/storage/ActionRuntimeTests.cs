using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Scheduling;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ActionRuntimeTests
{
    [Fact(DisplayName = "A Committed Completion Keeps The Waiting Startup Queue In The Same Drain Until Its Permanent Decline Commits")]
    public async Task DrainsWaitingStartup()
    {
        await using var fixture = new CompletionActionFixture();
        void EditQueue(Func<AutomationQueue, AutomationQueue> update)
        {
            var catalog = fixture.Source.Definitions.Read();
            var change = new AutomationChanges(catalog.Revision, [], [], [], [], [update(catalog.Queues.Single())], [], []);
            fixture.Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "completion-wait-fixture", change), change, DateTimeOffset.UtcNow);
        }
        EditQueue(queue => queue with { RunOnStartup = true });
        var startup = new StartupQueueStore(fixture.Source.Instance, fixture.Source.Database, fixture.Source.Lease, fixture.Base.Resources, TimeProvider.System);
        _ = startup.Commit(); Assert.Single(startup.ReadWaiting());
        EditQueue(queue => queue with { RunOnStartup = false });
        var receipt = await fixture.RunToReceipt();
        Assert.Equal(HostLifecyclePhase.Draining, fixture.Base.Lifecycle.Phase);
        Assert.NotNull(fixture.Completion.PendingOperationId); Assert.Equal(0, fixture.Power.Calls);
        Assert.Null(fixture.Completion.Read(fixture.Completion.PendingOperationId.Value));
        Assert.Null(fixture.Base.Resources.TryAcquire(Guid.NewGuid(), [new("unrelated", ResourceClaimMode.Mutation)]).Lease);
        var dispatch = new StartupQueueDispatch(fixture.Source.Instance, fixture.Source.Database, fixture.Source.Lease, startup, fixture.Base.Service, TimeProvider.System);
        dispatch.Tick(default, canCommit: false); Assert.Empty(startup.ReadWaiting());
        var countdown = await fixture.WaitFor(CompletionEffectPhase.Countdown);
        await fixture.Completion.CancelAsync(countdown.OperationId, CompletionActionStore.HostSubject, default);
        Assert.Equal(0, fixture.Power.Calls);
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
    }

    [Fact(DisplayName = "An Actual Restored Run Registers Its Frozen Queue And Cancels The Countdown Without Any System Call")]
    public async Task CancelsFrozenCountdown()
    {
        await using var fixture = new CompletionActionFixture();
        var (receipt, countdown) = await fixture.RunToCountdown();
        Assert.Equal(0, fixture.Power.Calls);
        Assert.Null(fixture.Base.Resources.TryAcquire(Guid.NewGuid(), [new("unrelated", ResourceClaimMode.Mutation)]).Lease);
        await Assert.ThrowsAsync<IOException>(() => fixture.Completion.ExecuteAsync(new CopiedReservation(countdown), default));
        Assert.Equal(0, fixture.Power.Calls);
        await fixture.Completion.CancelAsync(countdown.OperationId, CompletionActionStore.HostSubject, default);
        await fixture.WaitFor(CompletionEffectPhase.Cancelled, countdown.OperationId);
        Assert.Equal(0, fixture.Power.Calls); Assert.Equal(0, fixture.Exit.Requests);
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
        using var next = fixture.Base.Resources.TryAcquire(Guid.NewGuid(), [new("unrelated", ResourceClaimMode.Mutation)]).Lease;
        Assert.NotNull(next);
    }

    [Fact(DisplayName = "A Real Sixty Second Countdown Invokes Only The Controlled Power Port After The Actual Run Has Restored")]
    public async Task WaitsForCountdown()
    {
        await using var fixture = new CompletionActionFixture();
        var (receipt, countdown) = await fixture.RunToCountdown();
        Assert.Equal(TimeSpan.FromSeconds(60), countdown.NotBeforeUtc - countdown.AcceptedAtUtc);
        Assert.Equal(0, fixture.Power.Calls);
        var accepted = await fixture.WaitFor(CompletionEffectPhase.Accepted, countdown.OperationId, seconds: 80);
        Assert.True(fixture.ActualClock.FirstTimestamp > 0);
        Assert.True((double)(fixture.Power.CalledTimestamp - fixture.ActualClock.FirstTimestamp) / Stopwatch.Frequency >= 60);
        Assert.Equal("fixture_os_request_accepted", accepted.ReasonCode);
        Assert.Equal(1, fixture.Power.Calls); Assert.Equal(AutomationCompletionAction.Shutdown, fixture.Power.Action);
        var exitWait = Stopwatch.StartNew();
        while (fixture.Exit.Requests == 0 && exitWait.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
        Assert.Equal(1, fixture.Exit.Requests);
        fixture.Completion.OnCommitted(receipt);
        await Task.Delay(100);
        Assert.Equal(1, fixture.Power.Calls);
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
    }

    private sealed class CopiedReservation(CompletionActionSnapshot snapshot) : IPreparedCompletionAction
    {
        public CompletionActionSnapshot Snapshot => snapshot;
        public void Dispose() { }
    }
}
