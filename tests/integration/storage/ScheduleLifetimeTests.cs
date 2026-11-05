using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Scheduling;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ScheduleLifetimeTests
{
    [Fact]
    public async Task ReportsLoopFailure()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source;
        fixture.Execute("CREATE TRIGGER fixture_schedule_failure BEFORE INSERT ON schedule_minute_claims BEGIN SELECT RAISE(ABORT,'fixture_schedule_failure'); END;");
        var service = new PersistentScheduleService(source.Instance, source.Database, source.Lease,
            fixture.Resources, fixture.Lifecycle, fixture.Index, fixture.Service);
        service.Start(); await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.IsRunning); Assert.Equal("schedule_reconciliation_required", service.FailureCode);
        Assert.False(fixture.Index.Current.IsReady); await service.StopAsync();
    }

    [Fact(DisplayName = "Automatic Update Idle Check Includes Unissued Startup Work Waiting Occurrences And The Upcoming Window")]
    public async Task RespectsWaitingWork()
    {
        var clock = new Clock(); using var fixture = new ScheduleNativeFixture(clock, nextMinute: true); var source = fixture.Source;
        var service = new PersistentScheduleService(source.Instance, source.Database, source.Lease,
            fixture.Base.Resources, fixture.Base.Lifecycle, fixture.Base.Index, fixture.Base.Service, clock);
        try
        {
            Assert.True(service.BlocksAutomaticUpdate(TimeSpan.Zero));
            new StartupQueueStore(source.Instance, source.Database, source.Lease, fixture.Base.Resources, clock).Commit();
            Assert.False(service.BlocksAutomaticUpdate(TimeSpan.Zero)); Assert.True(service.BlocksAutomaticUpdate(TimeSpan.FromMinutes(5)));
            clock.Now = fixture.TriggerUtc;
            var waiting = fixture.CommitOnly();
            var catalog = source.Definitions.Read();
            var change = new NexusPipeline.Core.Automation.AutomationChanges(catalog.Revision, [], [], [], [], [],
                [catalog.Schedules.Single() with { IsEnabled = false }], []);
            source.Definitions.Commit(NexusPipeline.Platform.Storage.Automation.AutomationDefinitionStore.Identity(Guid.NewGuid(), "idle-fixture", change), change, clock.Now);
            Assert.True(service.BlocksAutomaticUpdate(TimeSpan.FromMinutes(5)));
            fixture.Store.BlockWaiting(waiting, "schedule_source_unavailable");
            Assert.False(service.BlocksAutomaticUpdate(TimeSpan.FromMinutes(5)));
        }
        finally { await service.StopAsync(); }
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-23T10:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact(DisplayName = "A Stopped Service Cannot Start Another Loop With The Old Callback Cancellation Lifetime")]
    public async Task RejectsStoppedLoops()
    {
        using var fixture = new ExecutionAdmissionFixture();
        var source = fixture.Source;
        var service = new PersistentScheduleService(source.Instance, source.Database, source.Lease,
            fixture.Resources, fixture.Lifecycle, fixture.Index, fixture.Service);
        service.Start(); await service.StopAsync();
        var error = Assert.Throws<InvalidOperationException>(service.Start);
        Assert.Equal("schedule_service_stopped", error.Message);
        Assert.False(service.IsRunning); await service.StopAsync();
        Assert.Equal(0, fixture.Count("scheduled_provider_occurrences")); Assert.Equal(0, fixture.Count("attempt_receipts"));
    }
}
