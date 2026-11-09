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
        var registry = new RecoveryJournalRegistry(database);
        var retries = registry.ReadHistory().Where(item => item.Kind == JournalKind.FileCleanup && item.JournalId != journalId);
        var outbox = new FileEffectOutbox(database);
        Assert.Equal(JournalPhase.Committed, registry.ReadJournal(journalId).Phase);
        Assert.All(outbox.Read(journalId), item => Assert.Equal(FileEffectState.NotExecuted, item.State));
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
            Assert.Empty(retries);
            var restored = new NexusPipeline.Core.Settings.HostSettingsChange(store.Read().Revision, saved.Values);
            store.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "retention-settings", restored), restored, DateTimeOffset.UtcNow);
        }
        retention.Tick();
        Assert.Null(retention.FailureCode);
        if (manual)
        {
            Assert.All(plan.Items, item => blobs.Verify(item.Blob));
            Assert.Empty(retries);
            return;
        }
        Assert.All(plan.Items, item => Assert.False(File.Exists(Path.Combine(source.Instance.RuntimeRoot, item.Blob.RelativePath))));
        Assert.All(outbox.Read(journalId), item => Assert.Equal(FileEffectState.NotExecuted, item.State));
        var retry = Assert.Single(retries);
        Assert.Equal(JournalPhase.Committed, retry.Phase);
        retention.Tick();
        Assert.Equal(retry.JournalId, Assert.Single(retries).JournalId);
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddDays(8);
    }
}
