using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ScheduledContextTests
{
    [Fact(DisplayName = "A Scheduled Clock Read During Recovery Verification Waits For Publication And Still Dispatches The Minute")]
    public async Task WaitsForRecoveryPublication()
    {
        await using var fixture = new ScheduledContextFixture(holdPlanning: true);
        await fixture.Start(waitForInvocation: false);
        await fixture.WaitFile("scheduled-planning");
        var source = fixture.Base.Source;
        var index = fixture.Base.Index;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var evidence = new PausedEvidence(new SnapshotCompletionEvidence(source.Instance, source.Database, new(), fixture.Installed.Roots),
            () => { entered.SetResult(); release.Task.WaitAsync(timeout.Token).GetAwaiter().GetResult(); });
        var verification = Task.Run(() => source.Lease.PublishExecution(() =>
            new RecoveryCoordinator(new RecoveryJournalRegistry(source.Database), evidence, index).VerifyAndPublish()));
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            Assert.Equal("recovery_verification_in_progress", index.Current.GlobalBlockReason);
            fixture.Release("scheduled-planning-release");
            while (!fixture.Schedules.Completion.IsCompleted && source.Lease.ExecutionPublicationCount < 2)
                await Task.Delay(10, timeout.Token);
        }
        finally { release.TrySetResult(); }
        var failure = await Record.ExceptionAsync(async () => { _ = await verification; });
        var planningError = fixture.Marker("scheduled-planning-error");
        var clockFailure = File.Exists(planningError) ? File.ReadAllText(planningError) : null;
        Assert.True(failure is null, $"{fixture.Schedules.FailureCode} {clockFailure} {failure}");
        Assert.True((await verification).IsReady);
        await fixture.WaitFile("scheduled-entered");
        fixture.Release("scheduled-release");
        await fixture.WaitResult();
        Assert.Null(fixture.Schedules.FailureCode);
        Assert.True(index.Current.IsReady);
    }

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

    private sealed class PausedEvidence(ISnapshotCompletionEvidence evidence, Action pause) : ISnapshotCompletionEvidence
    {
        public void VerifyAll(IReadOnlyList<RegisteredJournal> registered)
        {
            pause();
            evidence.VerifyAll(registered);
        }

        public IReadOnlyList<SnapshotCompletionProjection> ReadPendingCompletions(RecoveryRegistrySnapshot verified)
            => evidence.ReadPendingCompletions(verified);
    }
}
