using NexusPipeline.Models;
using NexusPipeline.Services.Execution;
using NexusPipeline.Services.Update;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class UpdateAutomationServiceTests
{
    [Fact]
    public async Task AutomaticCheckRunsAfterInitialDelayAndUsesConfiguredInterval()
    {
        var settings = new AppSettings { UpdateCheckEnabled = true };
        int checks = 0;
        UpdateStatusSnapshot status = Status(available: false, @checked: true);
        var service = CreateService(
            settings,
            () => status,
            _ =>
            {
                Interlocked.Increment(ref checks);
                return Task.FromResult(status);
            },
            initialDelay: TimeSpan.Zero,
            interval: TimeSpan.FromMilliseconds(30));

        try
        {
            service.Start();
            await EventuallyAsync(() => Volatile.Read(ref checks) >= 2);

            UpdateAutomationSnapshot snapshot = service.GetSnapshot();
            Assert.True(snapshot.CheckEnabled);
            Assert.NotNull(snapshot.LastAutomaticCheckAt);
            Assert.NotNull(snapshot.NextAutomaticCheckAt);
        }
        finally
        {
            service.Stop();
        }
    }

    [Fact]
    public async Task DisabledCheckCanBeEnabledWithoutRestartingCoordinator()
    {
        var disabled = new AppSettings { UpdateCheckEnabled = false };
        var enabled = disabled.Clone();
        enabled.UpdateCheckEnabled = true;
        int checks = 0;
        UpdateStatusSnapshot status = Status(available: false, @checked: true);
        var service = CreateService(
            disabled,
            () => status,
            _ =>
            {
                Interlocked.Increment(ref checks);
                return Task.FromResult(status);
            },
            initialDelay: TimeSpan.FromMilliseconds(200),
            interval: TimeSpan.FromMilliseconds(200));

        try
        {
            service.Start();
            await Task.Delay(30);
            Assert.Equal(0, Volatile.Read(ref checks));

            disabled.UpdateCheckEnabled = true;
            service.OnSettingsChanged(new AppSettings { UpdateCheckEnabled = false }, enabled);
            await EventuallyAsync(() => Volatile.Read(ref checks) > 0);
        }
        finally
        {
            service.Stop();
        }
    }

    [Fact]
    public async Task AvailableUpdateIsDownloadedOnlyWhenIdleAutoUpdateIsEnabled()
    {
        var settings = new AppSettings { UpdateCheckEnabled = true, UpdateAutoApplyEnabled = true };
        UpdateStatusSnapshot status = Status(available: true, @checked: true);
        int downloads = 0;
        var service = CreateService(
            settings,
            () => status,
            _ => Task.FromResult(status),
            _ =>
            {
                downloads++;
                status = status with { State = UpdateState.Downloading };
                return null;
            },
            initialDelay: TimeSpan.Zero,
            interval: TimeSpan.FromHours(12));

        try
        {
            service.Start();
            await EventuallyAsync(() => Volatile.Read(ref downloads) == 1);
        }
        finally
        {
            service.Stop();
        }
    }

    [Fact]
    public async Task AvailableUpdateIsNotDownloadedWhenIdleAutoUpdateIsDisabled()
    {
        var settings = new AppSettings { UpdateCheckEnabled = true, UpdateAutoApplyEnabled = false };
        UpdateStatusSnapshot status = Status(available: true, @checked: true);
        int downloads = 0;
        var service = CreateService(
            settings,
            () => status,
            _ => Task.FromResult(status),
            _ =>
            {
                downloads++;
                return null;
            },
            initialDelay: TimeSpan.Zero,
            interval: TimeSpan.FromHours(12));

        try
        {
            service.Start();
            await Task.Delay(50);
            Assert.Equal(0, Volatile.Read(ref downloads));
        }
        finally
        {
            service.Stop();
        }
    }

    [Fact]
    public async Task ReadyUpdateWaitsForIdleAndAppliesAfterBlockerClears()
    {
        var settings = new AppSettings { UpdateCheckEnabled = true, UpdateAutoApplyEnabled = true };
        UpdateStatusSnapshot status = Status(available: true, @checked: true, state: UpdateState.Ready);
        bool blocked = true;
        int applies = 0;
        var service = CreateService(
            settings,
            () => status,
            _ => Task.FromResult(status),
            initialDelay: TimeSpan.Zero,
            interval: TimeSpan.FromHours(12),
            idleRetry: TimeSpan.FromMilliseconds(20),
            tryAcquireIdle: _ => blocked
                ? AutoUpdateIdleAttempt.Blocked(new AutoUpdateIdleBlocker("queue_soon", "队列将在 4 分钟后触发", "每日队列", DateTime.Now.AddMinutes(4)))
                : AutoUpdateIdleAttempt.Accepted(new HostMaintenanceLease()),
            apply: (lease, _) =>
            {
                lease.Dispose();
                applies++;
                status = status with { State = UpdateState.Applying };
                return UpdateApplyResult.Ok(false);
            });

        try
        {
            service.Start();
            await EventuallyAsync(() => service.GetSnapshot().WaitingForIdle);
            Assert.Equal(0, Volatile.Read(ref applies));

            blocked = false;
            await EventuallyAsync(() => Volatile.Read(ref applies) == 1);
        }
        finally
        {
            service.Stop();
        }
    }

    [Fact]
    public async Task ReadyUpdateIsNotAppliedWhenIdleAutoUpdateIsDisabled()
    {
        var settings = new AppSettings { UpdateCheckEnabled = true, UpdateAutoApplyEnabled = false };
        UpdateStatusSnapshot status = Status(available: true, @checked: true, state: UpdateState.Ready);
        int applies = 0;
        var service = CreateService(
            settings,
            () => status,
            _ => Task.FromResult(status),
            initialDelay: TimeSpan.Zero,
            interval: TimeSpan.FromHours(12),
            apply: (lease, _) =>
            {
                lease.Dispose();
                applies++;
                return UpdateApplyResult.Ok(false);
            });

        try
        {
            service.Start();
            await Task.Delay(50);
            Assert.Equal(0, Volatile.Read(ref applies));
        }
        finally
        {
            service.Stop();
        }
    }

    [Fact]
    public async Task SourceChangeDuringCheckSchedulesAnotherAutomaticCheck()
    {
        var settings = new AppSettings { UpdateCheckEnabled = true };
        UpdateStatusSnapshot status = Status(available: false, @checked: true);
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int checks = 0;
        var service = CreateService(
            settings,
            () => status,
            async _ =>
            {
                if (Interlocked.Increment(ref checks) == 1)
                {
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task;
                }
                return status;
            },
            initialDelay: TimeSpan.Zero,
            interval: TimeSpan.FromHours(12));

        try
        {
            service.Start();
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            AppSettings previous = settings.Clone();
            settings.UpdateSourceUrl = "http://127.0.0.1:1/new-source";
            service.OnSettingsChanged(previous, settings);
            releaseFirst.TrySetResult(true);
            await EventuallyAsync(() => Volatile.Read(ref checks) >= 2);
        }
        finally
        {
            releaseFirst.TrySetResult(true);
            service.Stop();
        }
    }

    private static UpdateAutomationService CreateService(
        AppSettings settings,
        Func<UpdateStatusSnapshot> getStatus,
        Func<string, Task<UpdateStatusSnapshot>> check,
        Func<string, string?>? startDownload = null,
        Func<HostMaintenanceLease, string, UpdateApplyResult>? apply = null,
        Func<TimeSpan, AutoUpdateIdleAttempt>? tryAcquireIdle = null,
        Func<bool>? isAutomaticApplyAllowed = null,
        TimeSpan? initialDelay = null,
        TimeSpan? interval = null,
        TimeSpan? idleRetry = null)
    {
        return new UpdateAutomationService(
            () => settings,
            getStatus,
            check,
            startDownload ?? (_ => null),
            apply ?? ((lease, _) =>
            {
                lease.Dispose();
                return UpdateApplyResult.Ok(false);
            }),
            tryAcquireIdle ?? (_ => AutoUpdateIdleAttempt.Blocked(new AutoUpdateIdleBlocker(
                "host_busy",
                "宿主繁忙",
                null,
                null))),
            () => { },
            isAutomaticApplyAllowed: isAutomaticApplyAllowed,
            delay: static (delay, token) => Task.Delay(delay, token),
            initialCheckDelay: initialDelay,
            automaticCheckInterval: interval,
            idleRetryInterval: idleRetry);
    }

    private static UpdateStatusSnapshot Status(
        bool available,
        bool @checked,
        UpdateState state = UpdateState.Idle)
    {
        return new UpdateStatusSnapshot(
            state,
            null,
            0,
            0,
            "",
            "0.14.7",
            available ? "0.14.8" : null,
            available,
            "prerelease",
            available,
            "",
            @checked);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
        Assert.True(condition(), "条件在超时时间内未满足");
    }
}
