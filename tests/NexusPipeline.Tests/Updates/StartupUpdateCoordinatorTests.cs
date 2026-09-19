using Xunit;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Settings;
using NexusPipeline.Modules.Updates;

namespace NexusPipeline.Tests.Updates;

public sealed class StartupUpdateCoordinatorTests
{
    [Fact]
    public void IdleStartupChecksDownloadsAndAppliesBeforeServices()
    {
        string directory = CreateTestDirectory();
        try
        {
            var settings = new AppSettings { UpdateCheckEnabled = true, UpdateAutoApplyEnabled = true };
            var attempts = new StartupUpdateAttemptStore(Path.Combine(directory, "attempt.json"));
            var order = new List<string>();
            UpdateStatusSnapshot discovered = Status(UpdateState.Idle, available: true, latest: "0.16.6", canDownload: true);
            UpdateStatusSnapshot current = Status(UpdateState.Idle, available: false, latest: null, canDownload: false);
            var coordinator = new StartupUpdateCoordinator(
                () => settings,
                () => current.State,
                () => current,
                _ =>
                {
                    order.Add("check");
                    current = discovered;
                    return Task.FromResult(discovered);
                },
                _ =>
                {
                    order.Add("download");
                    current = discovered with { State = UpdateState.Ready };
                    return UpdateDownloadResult.Started();
                },
                () =>
                {
                    order.Add("wait");
                    return Task.CompletedTask;
                },
                () => { },
                defer =>
                {
                    Assert.False(defer);
                    order.Add("apply");
                    return UpdateApplyResult.Ok(deferred: false);
                },
                () => order.Add("record-check"),
                attempts);

            StartupUpdateDisposition disposition = coordinator.RunBeforeServices();

            Assert.Equal(StartupUpdateDisposition.RestartForUpdate, disposition);
            Assert.Equal(new[] { "check", "record-check", "download", "wait", "apply" }, order);
            Assert.Equal("0.16.6", attempts.ReadTargetVersion());
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, "0.16.6", false)]
    public void CheckWithoutInstallableUpdateDoesNotDownload(
        bool available,
        string? latest,
        bool canDownload)
    {
        string directory = CreateTestDirectory();
        try
        {
            var settings = new AppSettings { UpdateCheckEnabled = true, UpdateAutoApplyEnabled = true };
            UpdateStatusSnapshot checkedStatus = Status(UpdateState.Idle, available, latest, canDownload);
            int downloads = 0;
            var coordinator = CreateCoordinator(
                settings,
                () => UpdateState.Idle,
                () => checkedStatus,
                _ => Task.FromResult(checkedStatus),
                _ =>
                {
                    downloads++;
                    return UpdateDownloadResult.Started();
                },
                () => Task.CompletedTask,
                () => { },
                _ => UpdateApplyResult.Ok(false),
                new StartupUpdateAttemptStore(Path.Combine(directory, "attempt.json")));

            Assert.Equal(StartupUpdateDisposition.ContinueStartup, coordinator.RunBeforeServices());
            Assert.Equal(0, downloads);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public void CheckFailureContinuesStartupAndCancelsCurrentOperation()
    {
        string directory = CreateTestDirectory();
        try
        {
            var settings = new AppSettings { UpdateCheckEnabled = true, UpdateAutoApplyEnabled = true };
            int cancelCalls = 0;
            var coordinator = CreateCoordinator(
                settings,
                () => UpdateState.Idle,
                () => Status(UpdateState.Idle, false, null, false),
                _ => Task.FromException<UpdateStatusSnapshot>(new IOException("network unavailable")),
                _ => throw new InvalidOperationException("Failed check must not download"),
                () => Task.CompletedTask,
                () => cancelCalls++,
                _ => throw new InvalidOperationException("Failed check must not apply"),
                new StartupUpdateAttemptStore(Path.Combine(directory, "attempt.json")));

            Assert.Equal(StartupUpdateDisposition.ContinueStartup, coordinator.RunBeforeServices());
            Assert.Equal(1, cancelCalls);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public void DownloadFailureContinuesStartupAndCancelsCurrentOperation()
    {
        string directory = CreateTestDirectory();
        try
        {
            var settings = new AppSettings { UpdateCheckEnabled = true, UpdateAutoApplyEnabled = true };
            UpdateStatusSnapshot discovered = Status(UpdateState.Idle, true, "0.16.6", true);
            int cancelCalls = 0;
            var coordinator = CreateCoordinator(
                settings,
                () => UpdateState.Idle,
                () => discovered,
                _ => Task.FromResult(discovered),
                _ => UpdateDownloadResult.Started(),
                () => Task.FromException(new IOException("download interrupted")),
                () => cancelCalls++,
                _ => throw new InvalidOperationException("Failed download must not apply"),
                new StartupUpdateAttemptStore(Path.Combine(directory, "attempt.json")));

            Assert.Equal(StartupUpdateDisposition.ContinueStartup, coordinator.RunBeforeServices());
            Assert.Equal(1, cancelCalls);
        }
        finally
        {
            DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public void UnsafeRecoveryAbortsBeforeCheckingForUpdates()
    {
        var settings = new AppSettings { UpdateCheckEnabled = true, UpdateAutoApplyEnabled = true };
        int checks = 0;
        var coordinator = CreateCoordinator(
            settings,
            () => UpdateState.RecoveryPending,
            () => Status(UpdateState.RecoveryPending, false, null, false),
            _ =>
            {
                checks++;
                return Task.FromResult(Status(UpdateState.Idle, false, null, false));
            },
            _ => throw new InvalidOperationException("Unsafe recovery must not download"),
            () => Task.CompletedTask,
            () => { },
            _ => throw new InvalidOperationException("Unsafe recovery must not apply"),
            new StartupUpdateAttemptStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))),
            isStartupRecoveryUnsafe: () => true);

        Assert.Equal(StartupUpdateDisposition.AbortUnsafeRecovery, coordinator.RunBeforeServices());
        Assert.Equal(0, checks);
    }

    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 0)]
    public void ReadyUpdateIsAppliedBeforeServicesOnlyWhenAutomaticApplyIsEnabled(
        bool autoApplyEnabled,
        bool expectedRestart,
        int expectedApplyRequests)
    {
        string directory = Path.Combine(Path.GetTempPath(), "nxp-startup-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new AppSettings
            {
                UpdateCheckEnabled = true,
                UpdateAutoApplyEnabled = autoApplyEnabled,
            };
            var attempts = new StartupUpdateAttemptStore(Path.Combine(directory, "attempt.json"));
            int applyRequests = 0;
            int recordedChecks = 0;
            var status = new UpdateStatusSnapshot(
                UpdateState.Ready,
                null,
                0,
                0,
                "",
                "0.16.5",
                "0.16.6",
                true,
                "prerelease",
                true,
                "",
                true,
                true,
                true);
            var coordinator = new StartupUpdateCoordinator(
                () => settings,
                () => UpdateState.Ready,
                () => status,
                _ => Task.FromResult(status),
                _ => throw new InvalidOperationException("Ready state must not start a second download"),
                () => Task.CompletedTask,
                () => { },
                defer =>
                {
                    Assert.False(defer);
                    applyRequests++;
                    return UpdateApplyResult.Ok(deferred: false);
                },
                () => recordedChecks++,
                attempts);

            StartupUpdateDisposition disposition = coordinator.RunBeforeServices();

            Assert.Equal(expectedRestart
                ? StartupUpdateDisposition.RestartForUpdate
                : StartupUpdateDisposition.ContinueStartup, disposition);
            Assert.Equal(expectedApplyRequests, applyRequests);
            Assert.Equal(autoApplyEnabled ? 1 : 0, recordedChecks);
            Assert.Equal(autoApplyEnabled ? "0.16.6" : null, attempts.ReadTargetVersion());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ReadyUpdateWithRecentFailedTargetWaitsForCooldown()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nxp-startup-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new AppSettings { UpdateCheckEnabled = true, UpdateAutoApplyEnabled = true };
            var attempts = new StartupUpdateAttemptStore(Path.Combine(directory, "attempt.json"));
            attempts.Mark("0.16.6", DateTimeOffset.UtcNow);
            int applyRequests = 0;
            var status = new UpdateStatusSnapshot(
                UpdateState.Ready,
                null,
                0,
                0,
                "",
                "0.16.5",
                "0.16.6",
                true,
                "prerelease",
                true,
                "",
                true,
                true,
                true);
            var coordinator = new StartupUpdateCoordinator(
                () => settings,
                () => UpdateState.Ready,
                () => status,
                _ => Task.FromResult(status),
                _ => throw new InvalidOperationException("Ready state must not start a second download"),
                () => Task.CompletedTask,
                () => { },
                _ =>
                {
                    applyRequests++;
                    return UpdateApplyResult.Ok(deferred: false);
                },
                () => { },
                attempts);

            StartupUpdateDisposition disposition = coordinator.RunBeforeServices();

            Assert.Equal(StartupUpdateDisposition.ContinueStartup, disposition);
            Assert.Equal(0, applyRequests);
            Assert.Equal("0.16.6", attempts.ReadTargetVersion());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void FailedNetworkCheckPreservesTheTargetCooldownMarker()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nxp-startup-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new AppSettings { UpdateCheckEnabled = true, UpdateAutoApplyEnabled = true };
            var attempts = new StartupUpdateAttemptStore(Path.Combine(directory, "attempt.json"));
            attempts.Mark("0.16.6", DateTimeOffset.UtcNow);
            var failedStatus = new UpdateStatusSnapshot(
                UpdateState.Idle,
                null,
                0,
                0,
                "network unavailable",
                "0.16.5",
                null,
                null,
                "prerelease",
                false,
                "",
                true);
            var coordinator = new StartupUpdateCoordinator(
                () => settings,
                () => UpdateState.Idle,
                () => failedStatus,
                _ => Task.FromResult(failedStatus),
                _ => throw new InvalidOperationException("Failed check must not start a download"),
                () => Task.CompletedTask,
                () => { },
                _ => throw new InvalidOperationException("Failed check must not apply an update"),
                () => { },
                attempts);

            StartupUpdateDisposition disposition = coordinator.RunBeforeServices();

            Assert.Equal(StartupUpdateDisposition.ContinueStartup, disposition);
            Assert.Equal("0.16.6", attempts.ReadTargetVersion());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void SameTargetCanBeRetriedAutomaticallyAfterCooldownExpires()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nxp-startup-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new AppSettings { UpdateCheckEnabled = true, UpdateAutoApplyEnabled = true };
            var attempts = new StartupUpdateAttemptStore(Path.Combine(directory, "attempt.json"));
            attempts.Mark("0.16.6", DateTimeOffset.UtcNow - StartupUpdateAttemptStore.RetryCooldown - TimeSpan.FromMinutes(1));
            var discovered = new UpdateStatusSnapshot(
                UpdateState.Idle,
                null,
                0,
                0,
                "",
                "0.16.5",
                "0.16.6",
                true,
                "prerelease",
                true,
                "",
                true,
                true,
                true);
            UpdateStatusSnapshot status = discovered;
            int downloads = 0;
            int applyRequests = 0;
            var coordinator = new StartupUpdateCoordinator(
                () => settings,
                () => status.State,
                () => status,
                _ => Task.FromResult(discovered),
                _ =>
                {
                    downloads++;
                    status = discovered with { State = UpdateState.Ready };
                    return UpdateDownloadResult.Started();
                },
                () => Task.CompletedTask,
                () => { },
                defer =>
                {
                    Assert.False(defer);
                    applyRequests++;
                    return UpdateApplyResult.Ok(deferred: false);
                },
                () => { },
                attempts);

            StartupUpdateDisposition disposition = coordinator.RunBeforeServices();

            Assert.Equal(StartupUpdateDisposition.RestartForUpdate, disposition);
            Assert.Equal(1, downloads);
            Assert.Equal(1, applyRequests);
            Assert.Equal("0.16.6", attempts.ReadTargetVersion());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static StartupUpdateCoordinator CreateCoordinator(
        AppSettings settings,
        Func<UpdateState> getState,
        Func<UpdateStatusSnapshot> getStatus,
        Func<CancellationToken, Task<UpdateStatusSnapshot>> check,
        Func<CancellationToken, UpdateDownloadResult> startDownload,
        Func<Task> waitForCurrentOperation,
        Action cancelDownload,
        Func<bool, UpdateApplyResult> requestApply,
        StartupUpdateAttemptStore attempts,
        Func<bool>? isStartupRecoveryUnsafe = null) =>
        new(
            () => settings,
            getState,
            getStatus,
            check,
            startDownload,
            waitForCurrentOperation,
            cancelDownload,
            requestApply,
            () => { },
            attempts,
            isStartupRecoveryUnsafe);

    private static UpdateStatusSnapshot Status(UpdateState state, bool available, string? latest, bool canDownload) =>
        new(
            state,
            null,
            0,
            0,
            "",
            "0.16.5",
            latest,
            latest is null ? null : true,
            "prerelease",
            available,
            "",
            true,
            true,
            canDownload);

    private static string CreateTestDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nxp-startup-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTestDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
