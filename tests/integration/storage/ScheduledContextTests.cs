using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ScheduledContextTests
{
    [Theory(DisplayName = "Actual Installed Planned Work Borrows Only Its Live Accepted Root During Drain And Still Checks Current Preference")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BorrowsAcceptedRoot(bool disable)
    {
        await using var fixture = new ScheduledContextFixture();
        await fixture.Start();
        if (disable) fixture.Disable();
        using var drain = fixture.Base.Lifecycle.BeginCompletionDrain(Guid.NewGuid());
        Assert.NotNull(drain);
        Assert.Equal(HostLifecyclePhase.Draining, fixture.Base.Lifecycle.Phase);
        using (var denied = fixture.Base.Resources.TryAcquire(Guid.NewGuid(),
            [new("extension:fixture-plugin", ResourceClaimMode.Reference)]).Lease)
            Assert.Null(denied);
        var context = JsonSerializer.Deserialize<ScheduledPlannedInvocation>(
            File.ReadAllBytes(fixture.Marker("scheduled-context.json")))!;
        await using (var wrongScope = new ExtensionScope("fixture-plugin"))
            Assert.Throws<UnauthorizedAccessException>(() => fixture.Schedules.PrepareInstalledCallback(wrongScope, context));
        try
        {
            fixture.Release("scheduled-release");
            await fixture.WaitResult();
            var state = fixture.State();
            if (disable)
            {
                Assert.True(File.Exists(fixture.Marker("scheduled-borrow-denied")));
                Assert.False(File.Exists(fixture.Marker("scheduled-borrowed")));
                Assert.Null(state.Result!.Returned);
            }
            else
            {
                Assert.True(File.Exists(fixture.Marker("scheduled-borrowed")));
                Assert.NotNull(state.Result!.Returned);
                fixture.Release("scheduled-late-release");
                await fixture.WaitFile("scheduled-late-rejected");
                Assert.False(File.Exists(fixture.Marker("scheduled-late-effect")));
            }
            Assert.False(File.Exists(fixture.Marker("cleaned.json")));
            await fixture.Schedules.StopAsync();
            await fixture.Installed.Activator.StopAllAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(fixture.Marker("cleaned.json")));
        }
        finally
        {
            fixture.Release("scheduled-release"); fixture.Release("scheduled-late-release");
        }
    }
}
