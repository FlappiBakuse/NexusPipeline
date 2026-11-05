using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ScheduledResourceTests
{
    [Theory(DisplayName = "A Transient Actual Resource Conflict Is Rescanned In The Same Minute Without Replaying An Accepted Source")]
    [InlineData("extension:scheduled-fixture")]
    [InlineData("notifications:catalog")]
    public async Task RescansTransientConflicts(string resource)
    {
        await using var fixture = new NotificationScheduledFixture();
        await fixture.Activate();
        using (var blocker = fixture.Base.Resources.TryAcquire(Guid.NewGuid(), [new(resource, ResourceClaimMode.Mutation)]).Lease)
        {
            Assert.NotNull(blocker); await fixture.Tick.TickAsync();
            Assert.Empty(fixture.States()); Assert.Equal(0, fixture.PlannedCalls); Assert.Equal(0, fixture.Effects);
            Assert.Equal(0, fixture.Base.Count("notification_scheduled_sources"));
        }
        await fixture.Tick.TickAsync(); await fixture.PumpUntilResult();
        var state = Assert.Single(fixture.States()); Assert.Equal("returned", state.Result!.Completion);
        Assert.Equal(fixture.Clock.Initial, state.Source.Document.ObservedAtUtc);
        Assert.Equal(1, fixture.PlannedCalls); Assert.Equal(1, fixture.Effects);
        await fixture.Tick.TickAsync();
        Assert.Single(fixture.States()); Assert.Equal(1, fixture.Effects);
        Assert.Equal(1, fixture.Base.Count("scheduled_provider_occurrences")); Assert.Equal(0, fixture.Base.Count("scheduled_provider_pending"));
    }
}
