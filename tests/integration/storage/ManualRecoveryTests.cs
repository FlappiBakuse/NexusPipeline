using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ManualRecoveryTests(ITestOutputHelper output)
{
    [Theory(DisplayName = "A Manual Run Requires Complete Actual Writer Stop Evidence And Never Automatically Replays An Interrupted Callback")]
    [InlineData("accepted", false)]
    [InlineData("begun", false)]
    [InlineData("accepted", true)]
    [InlineData("begun", true)]
    public async Task RequiresStoppedWriter(string phase, bool startup)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "manual-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var reportPath = Path.Combine(root, "fault.json");
        using var owner = new OwnedCrashProcess();
        _ = owner.Start(["manual-work", root, phase, reportPath], root, Guid.NewGuid(), Guid.NewGuid(), Path.Combine(root, "native.json"));
        var report = await OwnedCrashProcess.ReadReportAsync(reportPath);
        Assert.Equal("faultReady", report.GetProperty("status").GetString());
        var instance = report.GetProperty("instance").Deserialize<RuntimeInstance>()!;
        var operation = report.GetProperty("operationId").GetGuid(); var task = report.GetProperty("taskId").GetGuid();
        var database = StateDatabase.OpenExisting(instance, Path.Combine(root, "inspection"));
        ManualWorkState? pending = null;
        ManualWorkInventory.Visit(instance, database, new(), state => pending = state);
        Assert.NotNull(pending); Assert.Null(pending.Result);
        Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveProcess(pending.Source.Document.Creator.Process));
        var before = File.ReadAllBytes(database.DatabasePath);
        Assert.Equal("manual_work_owner_unconfirmed", Assert.Throws<IOException>(() => StateDatabase.OpenExisting(instance,
            Path.Combine(root, "live-startup"), new JournalStartupAdmission(instance, new()))).Message);
        Assert.Equal(before, File.ReadAllBytes(database.DatabasePath));
        Assert.True((await owner.StopAsync()).CompleteJobStopped);
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(pending.Source.Document.Creator.Process));
        using var lease = InstanceLease.Acquire(instance, new());
        database = StateDatabase.OpenExisting(instance, Path.Combine(root, "stopped-startup"), new JournalStartupAdmission(instance, new()));
        var index = new StartupJournalRecovery(instance, new(), database,
            new(Path.Combine(root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
        var lifecycle = new HostLifecycle(index, TimeProvider.System);
        var resources = new ResourceLeaseManager(lifecycle);
        if (startup)
        {
            var execution = new ExecutionAdmissionService(instance, database, new(), lease, resources, index, new(), new(5, 6, 7));
            lease.BindDatabase(database);
            var auditPages = 0;
            var separatePendingScans = 0;
            SQLitePCL.strdelegate_trace trace = (_, sql) =>
            {
                if (sql.Contains("FROM main.manual_work_runs WHERE operation_id>", StringComparison.Ordinal)) auditPages++;
                if (sql.Contains("SELECT m.operation_id,m.relative_path,m.size_bytes,m.sha256", StringComparison.Ordinal) ||
                    sql.Contains("INSERT INTO temp.manual_work_pending", StringComparison.Ordinal) ||
                    sql.Contains("SELECT COUNT(*) FROM main.manual_work_runs m LEFT JOIN main.manual_work_result", StringComparison.Ordinal))
                    separatePendingScans++;
            };
            var handles = new[] { Observe(true, DatabaseDomain.Runtime), Observe(true, DatabaseDomain.Runtime | DatabaseDomain.System),
                Observe(false, DatabaseDomain.All) };
            try
            {
                var scheduling = new PersistentScheduleService(instance, database, lease, resources, lifecycle, index, execution);
                Assert.Null(scheduling.FailureCode);
            }
            finally
            {
                foreach (var handle in handles) SQLitePCL.raw.sqlite3_trace(handle, (SQLitePCL.strdelegate_trace?)null, null);
            }
            output.WriteLine($"cold scheduling startup: {auditPages} audit pages, {separatePendingScans} separate manual pending scans or projection builds");
            Assert.Equal(2, auditPages);
            Assert.Equal(0, separatePendingScans);

            SQLitePCL.sqlite3 Observe(bool readOnly, DatabaseDomain domains)
            {
                using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: readOnly, domains: domains);
                SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
                return connection.Handle!;
            }
        }
        var store = new ManualWorkStore(instance, database, lease, resources, new(), TimeProvider.System);
        if (!startup) Assert.True(store.ReconcilePriorOwners());
        var recovered = store.Lookup(operation, "fixture:caller", "fixture", "tasks", task, 7)!;
        Assert.Equal(phase == "accepted" ? "notStarted" : "unknown", recovered.Result!.Completion);
        Assert.Null(recovered.Result.Returned); Assert.Null(recovered.Result.FinalConsumedMilliseconds);
        var original = recovered.ResultReference;
        Assert.True(store.ReconcilePriorOwners());
        Assert.Equal(original, store.Lookup(operation, "fixture:caller", "fixture", "tasks", task, 7)!.ResultReference);
        lease.Dispose();
        await lease.WhenExecutionPublicationsReleased;
        _ = StateDatabase.OpenExisting(instance, Path.Combine(root, "reconciled-startup"), new JournalStartupAdmission(instance, new()));
    }
}
