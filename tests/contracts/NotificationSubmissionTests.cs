using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class NotificationSubmissionTests
{
    [Fact(DisplayName = "Notification Submissions Use Frozen Targets And Preserve The Hosts Idempotent Queue Acceptance")]
    public async Task PreservesQueueAcceptance()
    {
        var fixture = new ScheduledContractFixture();
        await using var scope = await fixture.ActivateAsync();
        var request = fixture.Submission();
        var first = await fixture.NotificationPort!.SubmitAsync(request, default);
        var repeated = await fixture.NotificationPort.SubmitAsync(request, default);
        Assert.True(first.IsNew);
        Assert.False(repeated.IsNew);
        Assert.Equal(first.AcceptanceId, repeated.AcceptanceId);
        Assert.Equal(fixture.Invocation.NotificationOperationId, first.OperationId);
        Assert.Equal(fixture.Invocation.NotificationTaskIds, fixture.Notifications.Last!.NotificationTaskIds);
        await Assert.ThrowsAsync<IOException>(() => fixture.NotificationPort.SubmitAsync(request with { Body = "different request" }, default).AsTask());
    }

    [Theory(DisplayName = "Unselected Forged Or Undeclared Notifications Never Reach The Queue")]
    [InlineData("unselected-target")]
    [InlineData("wrong-operation")]
    [InlineData("forged-invocation")]
    [InlineData("unknown-version")]
    [InlineData("missing-capability")]
    public async Task RejectsUnauthorizedTargets(string fault)
    {
        var fixture = new ScheduledContractFixture();
        await using var scope = await fixture.ActivateAsync(capabilities: fault == "missing-capability" ? ["schedule.register"] : null);
        var request = fixture.Submission();
        request = fault switch
        {
            "unselected-target" => request with { NotificationTaskIds = [Guid.NewGuid().ToString("D")] },
            "wrong-operation" => request with { OperationId = Guid.NewGuid().ToString("D") },
            "forged-invocation" => request with { Invocation = request.Invocation with { AttemptId = Guid.NewGuid().ToString("D") } },
            "unknown-version" => request with { Version = 17 },
            _ => request
        };
        var action = () => fixture.NotificationPort!.SubmitAsync(request, default).AsTask();
        if (fault == "unknown-version") await Assert.ThrowsAsync<JsonException>(action);
        else if (fault is "unselected-target" or "wrong-operation") await Assert.ThrowsAsync<ArgumentException>(action);
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(action);
        Assert.Equal(0, fixture.Notifications.Calls);
    }

    [Fact(DisplayName = "Notification Ports Cannot Submit Before Publication Or After Activation Disposal")]
    public async Task RequiresPublishedScope()
    {
        var fixture = new ScheduledContractFixture();
        await using var scope = await fixture.ActivateAsync(async () =>
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.NotificationPort!.SubmitAsync(fixture.Submission(), default).AsTask()));
        await scope.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.NotificationPort!.SubmitAsync(fixture.Submission(), default).AsTask());
        Assert.Equal(0, fixture.Notifications.Calls);
    }

    [Fact(DisplayName = "An Actually Accepted Notification Keeps Its Receipt Even When Cancellation Arrives Immediately After Commit")]
    public async Task KeepsCommittedReceipts()
    {
        var fixture = new ScheduledContractFixture();
        await using var scope = await fixture.ActivateAsync();
        using var cancelled = new CancellationTokenSource();
        fixture.Notifications.AfterCommit = cancelled.Cancel;
        var acceptance = await fixture.NotificationPort!.SubmitAsync(fixture.Submission(), cancelled.Token);
        Assert.True(cancelled.IsCancellationRequested);
        Assert.True(acceptance.IsNew);
        Assert.NotEqual(Guid.Empty, acceptance.AcceptanceId);
    }

    [Fact(DisplayName = "A Failed Notification Queue Cannot Produce An Acceptance Receipt")]
    public async Task PropagatesQueueFailure()
    {
        var fixture = new ScheduledContractFixture();
        await using var scope = await fixture.ActivateAsync();
        fixture.Notifications.Fail = true;
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.NotificationPort!.SubmitAsync(fixture.Submission(), default).AsTask());
        Assert.Equal("fixture_queue_failure", error.Message);
        Assert.Null(fixture.Notifications.Last);
    }

    [Fact(DisplayName = "Notification Target Arrays Cannot Be Changed After The Submission Crosses The Sdk Boundary")]
    public async Task FreezesTargetArrays()
    {
        var fixture = new ScheduledContractFixture();
        await using var scope = await fixture.ActivateAsync();
        var request = fixture.Submission();
        await fixture.NotificationPort!.SubmitAsync(request, default);
        ((string[])request.NotificationTaskIds)[0] = Guid.NewGuid().ToString("D");
        ((string[])request.Invocation.NotificationTaskIds)[0] = Guid.NewGuid().ToString("D");
        Assert.Equal(fixture.Invocation.NotificationTaskIds, fixture.Notifications.Last!.NotificationTaskIds);
        Assert.True(fixture.Invocation.Matches(fixture.Notifications.Last.Invocation));
    }
}
