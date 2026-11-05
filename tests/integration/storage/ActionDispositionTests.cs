using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Windows;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ActionDispositionTests
{
    [Theory(DisplayName = "A Controlled Acknowledgement Uses The Frozen Action And Hibernation Return Reopens Admission")]
    [InlineData(AutomationCompletionAction.Hibernate, 0)]
    [InlineData(AutomationCompletionAction.Restart, 1)]
    public async Task ReopensReturnedActions(
        AutomationCompletionAction action, int exitRequests)
    {
        var clock = new CompletionControlledClock();
        await using var fixture = new CompletionActionFixture(action, clock);
        var (_, countdown) = await fixture.RunToCountdown();
        clock.Advance(TimeSpan.FromSeconds(60));
        await fixture.WaitFor(CompletionEffectPhase.Accepted, countdown.OperationId);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(3) &&
            (action == AutomationCompletionAction.Hibernate ? fixture.Base.Lifecycle.Phase != HostLifecyclePhase.Running : fixture.Exit.Requests != 1))
            await Task.Delay(10);
        Assert.Equal(action, fixture.Power.Action); Assert.Equal(1, fixture.Power.Calls);
        Assert.Equal(exitRequests, fixture.Exit.Requests);
        if (action == AutomationCompletionAction.Hibernate)
        {
            using var next = fixture.Base.Resources.TryAcquire(Guid.NewGuid(), [new("unrelated", ResourceClaimMode.Mutation)]).Lease;
            Assert.NotNull(next);
        }
        else Assert.Equal(HostLifecyclePhase.ClosePermitted, fixture.Base.Lifecycle.Phase);
    }

    [Fact(DisplayName = "A Rejected System Request Records Failure And Reopens Admission Without Changing The Run")]
    public async Task RecordsRejectedActions()
    {
        var clock = new CompletionControlledClock();
        await using var fixture = new CompletionActionFixture(clock: clock);
        fixture.Power.Disposition = CompletionNativeDisposition.Rejected;
        var (receipt, countdown) = await fixture.RunToCountdown();
        clock.Advance(TimeSpan.FromSeconds(60)); await fixture.WaitFor(CompletionEffectPhase.Failed, countdown.OperationId);
        Assert.Equal(1, fixture.Power.Calls); Assert.Equal(0, fixture.Exit.Requests);
        Assert.Equal(HostLifecyclePhase.Running, fixture.Base.Lifecycle.Phase);
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
    }

    [Fact(DisplayName = "Exit Requests Only Graceful Host Exit Without Waiting Or Invoking Any Power Api")]
    public async Task RequestsGracefulExit()
    {
        var clock = new CompletionControlledClock();
        await using var fixture = new CompletionActionFixture(AutomationCompletionAction.Exit, clock);
        var receipt = await fixture.RunToReceipt();
        var accepted = await fixture.WaitFor(CompletionEffectPhase.Accepted, advanceClock: true);
        Assert.Equal(accepted.AcceptedAtUtc, accepted.NotBeforeUtc);
        Assert.Equal(1, fixture.Exit.Requests); Assert.Equal(0, fixture.Power.Calls);
        Assert.Equal(receipt, new AttemptReceiptStore(fixture.Source.Database).Read(receipt.Facts.AttemptId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Completion.CancelAsync(accepted.OperationId, CompletionActionStore.HostSubject, default));
    }
}
