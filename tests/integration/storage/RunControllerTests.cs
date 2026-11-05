using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class RunControllerTests
{
    private const string Raw = "{\"tasks\":{\"one\":true,\"new\":false},\"count\":41}";
    private const string Normalized = "{\"tasks\":{\"one\":false,\"new\":false},\"count\":41}";
    private const string Initial = "{\"tasks\":{\"one\":false},\"count\":17}";

    [Fact(DisplayName = "The Actual Timer Saves Both Normalized Resources While The Live Writer And Borrow Retain Ownership Until Finish")]
    public async Task PublishesSampledResources()
    {
        await using var fixture = new ControllerFixture();
        await fixture.StartAsync(); fixture.Root.Dispose();
        AssertBlocked(fixture); AssertPendingClaims(fixture);
        await Task.Delay(TimeSpan.FromSeconds(15));
        await WaitUntilAsync(() => fixture.Revisions().All(item => item.Revision == 2));
        Assert.Null(fixture.Controller.SynchronizationFailureCode);
        AssertSaved(fixture, 2, Normalized); AssertRaw(fixture);
        Assert.True(fixture.Controller.Read().SyncOrdinal >= 1);
        AssertBlocked(fixture);
        var result = await fixture.FinishAsync(Guid.NewGuid());
        Assert.Equal(ConfigurationRunPhase.Completed, result.Session.Phase);
        AssertSaved(fixture, 2, Normalized); fixture.Base.VerifyOriginal();
        Assert.Empty(fixture.Base.Index.Current.ResourceHolds);
        var admitted = fixture.Base.Resources.TryAcquire(Guid.NewGuid(), fixture.Root.Claims);
        using var lease = Assert.IsType<ResourceOperationLease>(admitted.Lease);
        Assert.Equal(ResourceLeaseDenial.None, admitted.Denial);
    }

    [Theory(DisplayName = "Disabling Finish Updates Preserves The Independent Initial Detection And Its Actual Persisted Preference")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesInitialDetection(bool waitForInitial)
    {
        await using var fixture = new ControllerFixture(updateAtFinish: false);
        var catalog = new NexusPipeline.Platform.Storage.Automation.AutomationDefinitionStore(fixture.Base.Source.Database).Read();
        Assert.False(Assert.Single(catalog.Scripts).UpdateConfigurationAtFinish);
        Assert.True(fixture.Prepared.Request.SyncInitially); Assert.False(fixture.Prepared.Request.SyncAtFinish);
        await fixture.StartAsync();
        if (waitForInitial)
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => fixture.Revisions().All(item => item.Revision == 2));
        }
        var result = await fixture.FinishAsync(Guid.NewGuid());
        Assert.All(result.Items, item => Assert.Equal("configuration_sync_disabled", item.ReasonCode));
        AssertSaved(fixture, waitForInitial ? 2 : 1, waitForInitial ? Normalized : Initial);
        fixture.Base.VerifyOriginal(); Assert.Null(fixture.Controller.SynchronizationFailureCode);
    }

    [Fact(DisplayName = "Initial Detection Runs Once And Later Changes Are Collected Only At Finalization")]
    public async Task SamplesInitialOnce()
    {
        await using var fixture = new ControllerFixture();
        await fixture.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(15));
        await WaitUntilAsync(() => fixture.Controller.Read().SyncOrdinal == 1);
        const string later = "{\"tasks\":{\"one\":true,\"new\":false},\"count\":42}";
        foreach (var resource in fixture.Base.Request.Resources)
            File.WriteAllText(Path.Combine(resource.Target.TargetPath, "tasks.json"), later);
        await Task.Delay(TimeSpan.FromSeconds(16));
        Assert.Equal(1, fixture.Controller.Read().SyncOrdinal);
        AssertSaved(fixture, 2, Normalized);
        Assert.Equal("configuration_run_initial_sync_consumed", (await Assert.ThrowsAsync<IOException>(() =>
            fixture.Controller.SynchronizeAsync(Guid.NewGuid()))).Message);
        var result = await fixture.FinishAsync(Guid.NewGuid());
        Assert.Equal(2, result.Session.SyncOrdinal);
        AssertSaved(fixture, 3, "{\"tasks\":{\"one\":false,\"new\":false},\"count\":42}");
        fixture.Base.VerifyOriginal(); Assert.Null(fixture.Controller.SynchronizationFailureCode);
    }

    [Theory(DisplayName = "Finishing Before The Initial Threshold Restores Originals And Publishes Both Validated Final Snapshots")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishesEarlyFinish(bool interrupted)
    {
        await using var fixture = new ControllerFixture();
        await fixture.StartAsync(); AssertRaw(fixture);
        Assert.Throws<InvalidOperationException>(() => fixture.Controller.Dispose());
        var result = await fixture.FinishAsync(Guid.NewGuid(), interrupted);
        Assert.Equal(interrupted ? ConfigurationRunPhase.Interrupted : ConfigurationRunPhase.Completed, result.Session.Phase);
        Assert.True(result.IsNew); Assert.Equal(1, result.Session.SyncOrdinal);
        Assert.All(result.Items, item =>
        {
            Assert.Equal("configuration_sync_allowed", item.ReasonCode);
            Assert.Equal(1, item.PreviousRevision); Assert.Equal(2, item.CurrentRevision);
        });
        AssertSaved(fixture, 2, Normalized); fixture.Base.VerifyOriginal();
        Assert.Empty(fixture.Base.Index.Current.ResourceHolds);
        Assert.Equal(2, fixture.Base.Count("journal_writer_job_roots"));
        Assert.Equal(2, fixture.Base.Count("journal_writer_job_closures"));
        Assert.Throws<IOException>(() => fixture.Controller.Start());
    }

    [Fact(DisplayName = "A Generic Run With Auto Sync Disabled Keeps Both Snapshots After Real Fifteen Second Execution And Finish")]
    public async Task HonorsDisabledSynchronization()
    {
        await using var fixture = new ControllerFixture(autoSync: false);
        await fixture.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(15));
        AssertSaved(fixture, 1, Initial); AssertRaw(fixture);
        Assert.Equal(0, fixture.Base.Count("configuration_run_syncs"));
        var result = await fixture.FinishAsync(Guid.NewGuid());
        Assert.All(result.Items, item => Assert.Equal("configuration_sync_disabled", item.ReasonCode));
        AssertSaved(fixture, 1, Initial); fixture.Base.VerifyOriginal();
        Assert.Null(fixture.Controller.SynchronizationFailureCode);
        Assert.Empty(fixture.Base.Index.Current.ResourceHolds);
    }

    [Fact(DisplayName = "A Second Progress Publication Failure Rolls Back Both Snapshots And Retains Restored Ownership Until The Same Finish Is Retried")]
    public async Task ReplaysFailedFinish()
    {
        await using var fixture = new ControllerFixture();
        fixture.Base.Execute("CREATE TRIGGER fixture_fail_run_progress BEFORE INSERT ON configuration_batch_items WHEN NEW.ordinal=1 BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        var operation = Guid.NewGuid();
        try
        {
            await fixture.StartAsync(); fixture.Root.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => fixture.Controller.SynchronizationFailureCode is not null);
            Assert.Equal("configuration_run_synchronization_failed", fixture.Controller.SynchronizationFailureCode);
            AssertSaved(fixture, 1, Initial); AssertRaw(fixture);
            Assert.Equal(0, fixture.Base.Count("configuration_batch_items"));
            Assert.Equal(0, fixture.Base.Count("configuration_run_syncs"));
            var operations = fixture.Base.Count("operations");
            await Assert.ThrowsAsync<SqliteException>(() => fixture.FinishAsync(operation));
            fixture.Base.VerifyOriginal(); AssertSaved(fixture, 1, Initial);
            Assert.Equal(operations, fixture.Base.Count("operations"));
            Assert.Equal(0, fixture.Base.Count("configuration_batch_items"));
            Assert.Equal(0, fixture.Base.Count("configuration_run_syncs"));
            Assert.Equal(ConfigurationRunPhase.Running, fixture.Controller.Read().Phase);
            Assert.True(fixture.Controller.Read().IsWriterAdmissionClosed);
            AssertPendingClaims(fixture); AssertBlocked(fixture);
        }
        finally { fixture.Base.Execute("DROP TRIGGER fixture_fail_run_progress;"); }
        var roots = fixture.Base.Count("journal_writer_job_roots");
        var closures = fixture.Base.Count("journal_writer_job_closures");
        var result = await fixture.FinishAsync(operation);
        Assert.True(result.IsNew); Assert.Equal(ConfigurationRunPhase.Completed, result.Session.Phase);
        AssertSaved(fixture, 2, Normalized); fixture.Base.VerifyOriginal();
        Assert.Equal(roots, fixture.Base.Count("journal_writer_job_roots"));
        Assert.Equal(closures, fixture.Base.Count("journal_writer_job_closures"));
        Assert.Equal(2, fixture.Base.Count("configuration_batch_items"));
        Assert.Empty(fixture.Base.Index.Current.ResourceHolds);
        Assert.False((await fixture.FinishAsync(operation)).IsNew);
    }

    [Fact(DisplayName = "A Finished Run Replays Only Its Exact Operation Subject And Terminal Outcome")]
    public async Task BindsTerminalReplay()
    {
        await using var fixture = new ControllerFixture(); await fixture.StartAsync();
        var operation = Guid.NewGuid(); var saved = await fixture.FinishAsync(operation);
        var count = fixture.Base.Count("operations");
        var replay = await fixture.FinishAsync(operation);
        Assert.False(replay.IsNew); Assert.Equal(saved.Session.SessionId, replay.Session.SessionId);
        Assert.Equal(saved.Items.ToArray(), replay.Items.ToArray());
        await Assert.ThrowsAsync<OperationConflictException>(() => fixture.FinishAsync(operation, interrupted: true));
        await Assert.ThrowsAsync<OperationConflictException>(() => fixture.Controller.SynchronizeAsync(operation));
        await Assert.ThrowsAsync<OperationConflictException>(() => fixture.FinishAsync(Guid.NewGuid()));
        var store = new RunProgressStore(fixture.Base.Source.Instance, fixture.Base.Source.Database, new());
        Assert.Throws<OperationConflictException>(() => store.Lookup(operation, "foreign-subject", saved.Session.SessionId, ConfigurationRunPhase.Completed));
        Assert.Equal(count, fixture.Base.Count("operations"));
        AssertSaved(fixture, 2, Normalized); fixture.Base.VerifyOriginal();
    }

    [Theory(DisplayName = "A Real Root Publication Failure Cannot Turn The Running Marker Into An Executed Final Sample")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsUnexecutedSamples(bool recoverCompletion)
    {
        await using var fixture = new ControllerFixture();
        fixture.Base.Execute("CREATE TRIGGER fixture_fail_root BEFORE INSERT ON journal_writer_job_roots BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        try { Assert.Throws<SqliteException>(() => fixture.Controller.Start()); }
        finally { fixture.Base.Execute("DROP TRIGGER fixture_fail_root;"); }
        var running = fixture.Controller.Read();
        Assert.True(running.EnteredRunning); Assert.Equal(ConfigurationRunPhase.Running, running.Phase);
        Assert.False(File.Exists(fixture.Base.NativeReport));
        var result = await fixture.FinishAsync(Guid.NewGuid(), interrupted: true);
        Assert.All(result.Items, item => Assert.Equal("configuration_sync_not_due", item.ReasonCode));
        Assert.Equal(0, result.Session.SyncOrdinal); AssertSaved(fixture, 1, Initial);
        fixture.Base.VerifyOriginal();
        Assert.All(fixture.ClosedWriters(), closure => { Assert.False(closure.WasExecuted); Assert.Equal(0, closure.RunElapsedMilliseconds); });
        ConfigurationRunInventory.VerifyReadOnly(fixture.Base.Source.Instance, fixture.Base.Source.Database, new());
        if (recoverCompletion)
        {
            using var sample = new RunProgressCapture(fixture.Base.Source.Instance, fixture.Base.Source.Database, new()).ReadRestored(result.Session);
            Assert.False(sample.WasExecuted); Assert.All(sample.Items, item => Assert.Equal("configuration_sync_not_due", item.ReasonCode));
        }
    }

    [Theory(DisplayName = "An Unstarted Or Partially Installed Run Closes Its Actual Empty Job And Finishes Interrupted Without Saving")]
    [InlineData("not-started")]
    [InlineData("second-install")]
    [InlineData("mark-running")]
    public async Task CollectsPartialInstallation(string boundary)
    {
        await using var fixture = new ControllerFixture();
        var session = fixture.Controller.Read(); var second = session.Resources[1].JournalId;
        Assert.Equal(2, fixture.Base.Count("journal_writer_jobs"));
        Assert.Equal(0, fixture.Base.Count("journal_writer_job_roots"));
        if (boundary != "not-started")
        {
            var trigger = boundary == "second-install"
                ? $"BEFORE UPDATE ON operation_journals WHEN NEW.journal_id='{second:D}' AND NEW.phase='applied'"
                : $"BEFORE UPDATE ON configuration_run_sessions WHEN NEW.session_id='{session.SessionId:D}' AND OLD.phase=0 AND NEW.phase=1";
            fixture.Base.Execute($"CREATE TRIGGER fixture_fail_run_start {trigger} BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
            try { Assert.Throws<SqliteException>(() => fixture.Controller.Start()); }
            finally { fixture.Base.Execute("DROP TRIGGER fixture_fail_run_start;"); }
            var journals = new JournalStore(fixture.Base.Source.Instance, new());
            var registered = new RecoveryJournalRegistry(fixture.Base.Source.Database).ReadSnapshot().Journals;
            foreach (var resource in session.Resources)
            {
                Assert.Equal(JournalPhase.Applied, journals.Read(resource.JournalId).Phase);
                var expected = boundary == "second-install" && resource.JournalId == second ? JournalPhase.Applying : JournalPhase.Applied;
                Assert.Equal(expected, Assert.Single(registered, item => item.JournalId == resource.JournalId).Phase);
            }
            Assert.All(fixture.Base.Request.Resources, resource => Assert.Equal("{\"tasks\":{\"one\":true},\"count\":17}",
                File.ReadAllText(Path.Combine(resource.Target.TargetPath, "tasks.json"))));
        }
        var pending = fixture.Controller.Read();
        Assert.Equal(ConfigurationRunPhase.Prepared, pending.Phase); Assert.Equal(1, pending.Revision);
        Assert.False(pending.EnteredRunning); Assert.False(File.Exists(fixture.Base.NativeReport));
        var result = await fixture.FinishAsync(Guid.NewGuid(), interrupted: true);
        Assert.Equal(ConfigurationRunPhase.Interrupted, result.Session.Phase); Assert.False(result.Session.EnteredRunning);
        Assert.Equal(0, result.Session.SyncOrdinal);
        Assert.All(result.Items, item => Assert.Equal("configuration_sync_not_due", item.ReasonCode));
        AssertSaved(fixture, 1, Initial); fixture.Base.VerifyOriginal();
        Assert.Empty(fixture.Base.Index.Current.ResourceHolds);
        Assert.Equal(0, fixture.Base.Count("journal_writer_job_roots"));
        Assert.Equal(2, fixture.Base.Count("journal_writer_job_closures"));
        var closures = fixture.ClosedWriters(); Assert.Equal(2, closures.Length);
        Assert.All(closures, closure => { Assert.False(closure.WasExecuted); Assert.Null(closure.Root); Assert.Empty(closure.Family); });
        ConfigurationRunInventory.VerifyReadOnly(fixture.Base.Source.Instance, fixture.Base.Source.Database, new());
    }

    private static void AssertRaw(ControllerFixture fixture) => Assert.All(fixture.Base.Request.Resources,
        resource => Assert.Equal(Raw, File.ReadAllText(Path.Combine(resource.Target.TargetPath, "tasks.json"))));
    private static void AssertSaved(ControllerFixture fixture, long revision, string value) => Assert.All(fixture.Revisions(), saved =>
    { Assert.Equal(revision, saved.Revision); Assert.Equal(value, fixture.Base.Read(saved.Tree)); });
    private static void AssertBlocked(ControllerFixture fixture)
    {
        var admission = fixture.Base.Resources.TryAcquire(Guid.NewGuid(), fixture.Root.Claims);
        using var unexpected = admission.Lease;
        Assert.Null(admission.Lease); Assert.Equal(ResourceLeaseDenial.HostUnavailable, admission.Denial);
    }
    private static void AssertPendingClaims(ControllerFixture fixture)
    {
        var holds = fixture.Base.Index.Current.ResourceHolds.Where(item => item.Owner == RecoveryHoldOwner.ConfigurationOperations).ToArray();
        foreach (var claim in fixture.Root.Claims)
        {
            var hold = Assert.Single(holds, item => item.OperationId == fixture.Base.Owner && item.ResourceKey == claim.ResourceKey);
            Assert.Equal(claim.Mode, hold.Mode);
        }
    }
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
        Assert.True(condition(), "The actual run monitor did not reach its expected durable state");
    }

    [SupportedOSPlatform("windows")]
    private sealed class ControllerFixture : IAsyncDisposable
    {
        internal ConfigurationRunFixture Base { get; }
        internal ResourceOperationLease Root { get; }
        internal PreparedConfigurationRun Prepared { get; }
        internal ConfigurationRunController Controller { get; }
        private bool _finished;

        internal ControllerFixture(bool autoSync = true, bool updateAtFinish = true)
        {
            Base = new(additional: true, nativeWriter: true, updateAtFinish: updateAtFinish);
            var request = Base.Request with { AutoSyncEnabled = autoSync, SyncInitially = autoSync, SyncAtFinish = autoSync && updateAtFinish };
            Root = Base.Acquire(request);
            Prepared = Base.Preparation.Prepare(Root, Base.Owner, Base.Execution.Plan, Base.StepId, request);
            var parent = Base.Parent(Prepared); Base.Accept(parent);
            var session = Guid.NewGuid(); var parentId = Guid.Parse(parent.JournalId);
            new ConfigurationRunStore(Base.Source.Instance, Base.Source.Database, new()).Begin(
                ConfigurationRunStore.BeginIdentity(session, ConfigurationRunFixture.Subject, parentId, Base.StepId, Prepared),
                parentId, Base.StepId, Prepared, DateTimeOffset.UtcNow);
            Controller = new(Base.Source.Instance, Base.Source.Database, new(), Base.Lease, Base.Index, session, ConfigurationRunFixture.Subject, Prepared);
        }

        internal async Task StartAsync()
        {
            var identity = Controller.Start();
            var report = await OwnedCrashProcess.ReadReportAsync(Base.NativeReport);
            Assert.Equal(identity.ProcessId, report.GetProperty("processId").GetInt32());
        }
        internal ConfigurationRevision[] Revisions()
        {
            var store = new ConfigurationRevisionStore(Base.Source.Instance, Base.Source.Database, new());
            return Base.Request.Resources.Select(resource => store.Read(resource.Target.Address)!).ToArray();
        }
        internal FrozenJobClosure[] ClosedWriters()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Base.Source.Database.DatabasePath, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT c.journal_id,c.writer_id,c.job_name,j.session_id FROM journal_writer_job_closures c JOIN journal_writer_jobs j " +
                "ON j.journal_id=c.journal_id AND j.writer_id=c.writer_id AND j.job_name=c.job_name;";
            var keys = new List<(Guid Journal, Guid Writer, string Job, int Session)>();
            using (var rows = command.ExecuteReader())
                while (rows.Read()) keys.Add((Guid.Parse(rows.GetString(0)), Guid.Parse(rows.GetString(1)), rows.GetString(2), rows.GetInt32(3)));
            var store = new JobClosureStore(Base.Source.Instance, Base.Source.Database, new());
            return keys.Select(key => store.Read(key.Journal, key.Writer, key.Job, key.Session)!).ToArray();
        }
        internal async Task<RunSyncResult> FinishAsync(Guid operation, bool interrupted = false)
        {
            var result = await Controller.FinishAsync(operation, interrupted); _finished = true; return result;
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_finished) await FinishAsync(Guid.NewGuid(), interrupted: true);
                Controller.Dispose();
            }
            finally { Prepared.Dispose(); Root.Dispose(); Base.Dispose(); }
        }
    }
}
