using NexusPipeline.Services.Update;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class StartupUpdateCheckServiceTests
{
    [Fact]
    public async Task EnabledStartupCheckRunsOnceAfterInjectedDelay()
    {
        int delayCalls = 0;
        int checkCalls = 0;
        var status = new UpdateStatusSnapshot(
            UpdateState.Idle,
            null,
            0,
            0,
            "",
            "0.12.9",
            null,
            null,
            "prerelease",
            false,
            "",
            false);
        var service = new StartupUpdateCheckService(
            () => true,
            () =>
            {
                checkCalls++;
                return Task.FromResult(status);
            },
            _ =>
            {
                delayCalls++;
                return Task.CompletedTask;
            });

        await service.StartAsync();
        await service.StartAsync();

        Assert.Equal(1, delayCalls);
        Assert.Equal(1, checkCalls);
    }

    [Fact]
    public async Task DisabledStartupCheckDoesNotDelayOrCallUpdateService()
    {
        int delayCalls = 0;
        int checkCalls = 0;
        var service = new StartupUpdateCheckService(
            () => false,
            () =>
            {
                checkCalls++;
                return Task.FromResult(new UpdateStatusSnapshot(
                    UpdateState.Idle,
                    null,
                    0,
                    0,
                    "",
                    "0.12.9",
                    null,
                    null,
                    "prerelease",
                    false,
                    "",
                    false));
            },
            _ =>
            {
                delayCalls++;
                return Task.CompletedTask;
            });

        await service.StartAsync();

        Assert.Equal(0, delayCalls);
        Assert.Equal(0, checkCalls);
    }
}
