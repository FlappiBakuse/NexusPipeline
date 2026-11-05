using System.Runtime.Versioning;
using NexusPipeline.Extensibility.Protocol;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ScheduledRevocationTests
{
    [Fact(DisplayName = "A Revoked Actual Registration Cannot Submit And Its Old Invocation Cannot Bind To A Replacement Activation")]
    public async Task RejectsRevokedInvocations()
    {
        await using var fixture = new NotificationScheduledFixture { HoldCallback = true };
        await fixture.Activate(); await fixture.Tick.TickAsync();
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var context = Assert.IsType<ScheduledPlannedInvocation>(fixture.LastContext); var port = fixture.Port!;
        var activation = fixture.Scope!.ActivationId; await fixture.Scope.DisposeAsync();
        await fixture.PumpUntilResult();
        var ended = Assert.Single(fixture.States()); Assert.Equal("unknown", ended.Result!.Completion);
        var submission = new NotificationSubmission { Format = "nexus.notification.submission", Version = 1,
            Invocation = context.Invocation, OperationId = context.Invocation.NotificationOperationId,
            NotificationTaskIds = context.Invocation.NotificationTaskIds, Title = "revoked", Body = "must not enqueue" };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await port.SubmitAsync(submission, default));
        fixture.HoldCallback = false;
        await fixture.Activate(); Assert.NotEqual(activation, fixture.Scope!.ActivationId);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(async () => await fixture.Port!.SubmitAsync(submission, default));
        await fixture.Tick.TickAsync();
        var replay = Assert.Single(fixture.States()); Assert.Equal(ended.ResultReference, replay.ResultReference);
        Assert.Equal(1, fixture.PlannedCalls); Assert.Equal(1, fixture.Effects);
        Assert.Equal(0, fixture.Base.Count("notification_scheduled_sources")); Assert.Equal(0, fixture.Base.Count("notification_deliveries"));
    }
}
