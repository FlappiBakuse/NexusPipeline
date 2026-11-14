using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ScheduleLifetimeTests
{
    [Fact(DisplayName = "A Busy Rollback Commit Keeps The Scheduler Alive And The Same Minute Resumes After Its Reader Closes")]
    public async Task RetriesBusyMetadata()
    {
        var clock = new Clock(); using var fixture = new ScheduleNativeFixture(clock); var source = fixture.Source;
        var service = new PersistentScheduleService(source.Instance, source.Database, source.Lease,
            fixture.Base.Resources, fixture.Base.Lifecycle, fixture.Base.Index, fixture.Base.Service, clock);
        try
        {
            using (var connection = DatabaseConnections.Open(source.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT count(*) FROM main.operations;"; _ = command.ExecuteScalar();
                service.Start();
                Assert.True(service.IsRunning); Assert.Null(service.FailureCode); Assert.True(fixture.Base.Index.Current.IsReady);
            }
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (fixture.Base.Count("schedule_minute_claims") != 1 && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.Equal(1, fixture.Base.Count("schedule_minute_claims"));
            Assert.Null(service.FailureCode); Assert.True(service.IsRunning); Assert.True(fixture.Base.Index.Current.IsReady);
        }
        finally { await service.StopAsync(); }
    }

    [Fact]
    public async Task ReportsLoopFailure()
    {
        var clock = new Clock(); using var fixture = new ScheduleNativeFixture(clock); var source = fixture.Source;
        fixture.Base.Execute("CREATE TRIGGER main.fixture_schedule_failure BEFORE INSERT ON schedule_minute_claims BEGIN SELECT RAISE(ABORT,'fixture_schedule_failure'); END;");
        var service = new PersistentScheduleService(source.Instance, source.Database, source.Lease,
            fixture.Base.Resources, fixture.Base.Lifecycle, fixture.Base.Index, fixture.Base.Service, clock);
        service.Start(); await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.IsRunning); Assert.Equal("schedule_reconciliation_required:SqliteException", service.FailureCode);
        Assert.False(fixture.Base.Index.Current.IsReady); await service.StopAsync();
    }

    [Fact(DisplayName = "Empty Runtime Minutes Leave Persistent State Untouched Across Ticks And Restart")]
    public async Task SkipsEmptyMinutes()
    {
        using var fixture = new ExecutionAdmissionFixture(); var source = fixture.Source; var clock = new Clock();
        fixture.Execute("CREATE TRIGGER main.reject_empty_minute BEFORE INSERT ON schedule_minute_claims BEGIN SELECT RAISE(ABORT,'unexpected_empty_minute'); END;");
        var store = new ScheduleOccurrenceStore(source.Instance, source.Database, source.Lease, fixture.Resources, clock);
        var admission = new ScheduledExecutionAdmission(source.Instance, source.Database, source.Lease, fixture.Service, clock);
        var before = fixture.Count("operations");
        for (var restart = 0; restart < 2; restart++)
        {
            var runtime = new ScheduleRuntimeService(store, admission, clock); runtime.Initialize();
            for (var minute = 0; minute < 5; minute++)
            {
                await runtime.TickAsync(); await runtime.TickAsync(); clock.Now += TimeSpan.FromMinutes(1);
            }
            await runtime.StopAsync();
        }
        Assert.Equal(before, fixture.Count("operations")); Assert.Equal(0, fixture.Count("schedule_minute_claims"));
        Assert.Null(store.ReadCheckpoint().Watermark.HighestUtcMinute);
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
