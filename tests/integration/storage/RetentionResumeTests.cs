using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class RetentionResumeTests
{
    [Theory]
    [InlineData("AcceptanceCommitted")]
    [InlineData("JournalFlushed")]
    public Task ResumesInterruptedCleanup(string phase) => RunAsync(phase);

    [Fact]
    public Task RespectsChangedRetention() => RunAsync("BeforeCleanup", changeRetention: true);

    [Fact]
    public Task KeepsManualCleanup() => RunAsync("BeforeCleanup", manual: true);

    private static async Task RunAsync(string phase, bool changeRetention = false, bool manual = false)
    {
        using var fixture = new ExecutionHooksFixture(false, accept: false);
        var source = fixture.Source;
        var operation = Guid.NewGuid();
        source.Lease.Dispose();
        using var child = new OwnedCrashProcess();
        var report = Path.Combine(source.Root, "retirement-stop.json");
        child.Start(["retirement-crash", source.Instance.RuntimeRoot, source.BindingA.ToString("D"),
            operation.ToString("D"), phase, report, manual ? "local:manual-retention" : "host:history-retention"], source.Root, source.Instance.InstanceId, operation,
            Path.Combine(source.Root, "retirement-child.json"));
        var stoppedAt = await child.ReadReportAsync(report, TimeSpan.FromSeconds(30));
        Assert.Equal("faultReady", stoppedAt.GetProperty("status").GetString());
        var journalId = stoppedAt.GetProperty("journalId").GetGuid();
        var stopped = await child.StopAsync();
        Assert.True(stopped.CompleteJobStopped);
        var pending = new RecoveryJournalRegistry(source.Database).ReadJournal(journalId);
        Assert.Equal(JournalPhase.Prepared, pending.Phase);
        var blobs = new ImmutableBlobStore(source.Instance, new());
        var path = Path.Combine(source.Instance.RuntimeRoot, pending.PlanPath);
        var plan = new CleanupPlanCodec(source.Instance, new(), blobs).Read(new(pending.PlanPath, new FileInfo(path).Length, pending.PlanDigest));
        Assert.All(plan.Items, item => blobs.Verify(item.Blob));
        using var lease = InstanceLease.Acquire(source.Instance, new());
        var roots = new PayloadApplyRoots(AppContext.BaseDirectory, Path.Combine(source.Instance.RuntimeRoot, "extensions"));
        var database = StateDatabase.OpenExisting(source.Instance, Path.Combine(source.Root, "retention-reopen"),
            new JournalStartupAdmission(source.Instance, new(), roots));
        var index = new StartupJournalRecovery(source.Instance, new(), database, roots).Recover(lease);
        Assert.True(index.Current.IsReady);
        Assert.Empty(index.Current.Isolations);
        Assert.Equal(JournalPhase.Committed, new RecoveryJournalRegistry(database).ReadJournal(journalId).Phase);
        Assert.All(new FileEffectOutbox(database).Read(journalId), item => Assert.Equal(FileEffectState.NotExecuted, item.State));
        var service = new ContentRetirementService(source.Instance, database, new(), lease,
            new ResourceLeaseManager(new(index, TimeProvider.System)), index);
        var retention = new RecordHistoryRetention(database, service, new Clock());
        if (changeRetention)
        {
            var store = new HostSettingsStore(database);
            var saved = store.Read();
            var extended = new NexusPipeline.Core.Settings.HostSettingsChange(saved.Revision,
                saved.Values with { HistoryRetentionDays = 180 });
            store.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "retention-settings", extended), extended, DateTimeOffset.UtcNow);
            retention.Tick();
            Assert.All(plan.Items, item => blobs.Verify(item.Blob));
            var restored = new NexusPipeline.Core.Settings.HostSettingsChange(store.Read().Revision, saved.Values);
            store.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "retention-settings", restored), restored, DateTimeOffset.UtcNow);
        }
        retention.Tick();
        Assert.Null(retention.FailureCode);
        if (manual)
        {
            Assert.All(plan.Items, item => blobs.Verify(item.Blob));
            using var check = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
            using var count = check.CreateCommand();
            count.CommandText = "SELECT count(*) FROM operations WHERE operation_kind='records.retention.cleanup';";
            Assert.Equal(0L, count.ExecuteScalar());
            return;
        }
        Assert.All(plan.Items, item => Assert.False(File.Exists(Path.Combine(source.Instance.RuntimeRoot, item.Blob.RelativePath))));
        Assert.Equal(1, fixture.Base.Count("record_content_retirements"));
        Assert.All(new FileEffectOutbox(database).Read(journalId), item => Assert.Equal(FileEffectState.NotExecuted, item.State));
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT count(*) FROM operations WHERE operation_kind='records.retention.cleanup';";
        Assert.Equal(1L, query.ExecuteScalar());
        retention.Tick();
        Assert.Equal(1L, query.ExecuteScalar());
        Console.WriteLine($"phase={phase}; startup=NotExecuted; nextTick=reauthorizedCleanup; operation={operation:D}");
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddDays(8);
    }
}
