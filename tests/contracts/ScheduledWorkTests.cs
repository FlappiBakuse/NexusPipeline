using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class ScheduledWorkTests
{
    [Fact(DisplayName = "Scheduled Providers Remain Invisible Until Activation And Only Handle Explicit Host Invocations")]
    public async Task BindsHostInvocations()
    {
        var fixture = new ScheduledContractFixture();
        await using var scope = await fixture.ActivateAsync(async () =>
        {
            Assert.Empty(fixture.Registry.PublishedProviderIds("fixture"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DispatchAsync().AsTask());
        });
        Assert.Equal(0, fixture.Calls);
        Assert.Equal("daily", Assert.Single(fixture.Registry.PublishedProviderIds("fixture")));
        var result = await fixture.DispatchAsync();
        Assert.Equal(BusinessOutcome.NotApplicable, result.Business);
        Assert.Equal("fixture_work_observed", result.ReasonCode);
        Assert.Equal(1, fixture.Calls);
    }

    [Fact(DisplayName = "Failed Scheduled Activation Revokes Every Registration And Any Retained Registration Port")]
    public async Task RevokesFailedActivation()
    {
        var fixture = new ScheduledContractFixture();
        await Assert.ThrowsAsync<IOException>(() => fixture.ActivateAsync(() => throw new IOException("fixture_activation_failure")));
        Assert.Empty(fixture.Registry.PublishedProviderIds("fixture"));
        Assert.True(fixture.Scope!.Cancellation.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RegisterRetainedAsync().AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DispatchAsync().AsTask());
        Assert.Equal(0, fixture.Calls);
    }

    [Fact(DisplayName = "Missing Schedule Capability Fails Before Registering Any Work Provider")]
    public async Task RequiresScheduleCapability()
    {
        var fixture = new ScheduledContractFixture();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.ActivateAsync(capabilities: ["notification.submit"]));
        Assert.Empty(fixture.Registry.PublishedProviderIds("fixture"));
        Assert.Equal(0, fixture.Calls);
    }

    [Theory(DisplayName = "Late Scheduled Results Cannot Outlive Their Activation Or Frozen Host Authorization")]
    [InlineData("extension")]
    [InlineData("authorization")]
    public async Task RejectsLateResults(string revoked)
    {
        var fixture = new ScheduledContractFixture();
        var entered = new TaskCompletionSource<ScheduledInvocation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Work = async (request, _) => { entered.SetResult(request); await release.Task; return ScheduledContractFixture.Result(request); };
        await using var scope = await fixture.ActivateAsync();
        var pending = fixture.DispatchAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (revoked == "extension") await scope.DisposeAsync();
        else fixture.Authorization.Allowed = false;
        release.SetResult();
        if (revoked == "extension") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pending);
    }

    [Theory(DisplayName = "Invalid Scheduled Results Cannot Become Accepted Business Facts")]
    [InlineData("unknown-version")]
    [InlineData("wrong-invocation")]
    [InlineData("unknown-business")]
    [InlineData("invalid-reason")]
    public async Task RejectsInvalidResults(string fault)
    {
        var fixture = new ScheduledContractFixture();
        fixture.Work = (request, _) =>
        {
            var result = ScheduledContractFixture.Result(request);
            return ValueTask.FromResult(fault switch
            {
                "unknown-version" => result with { Version = 17 },
                "wrong-invocation" => result with { Invocation = request with { OccurrenceId = Guid.NewGuid().ToString("D") } },
                "unknown-business" => result with { BusinessOutcome = "probably-fine" },
                _ => result with { ReasonCode = "unbounded detail instead of code" }
            });
        };
        await using var scope = await fixture.ActivateAsync();
        await Assert.ThrowsAsync<JsonException>(() => fixture.DispatchAsync().AsTask());
        Assert.Equal(1, fixture.Calls);
    }
}
