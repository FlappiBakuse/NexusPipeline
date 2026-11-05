using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ManualRecoveryTests
{
    [Theory(DisplayName = "A Manual Run Requires Complete Actual Writer Stop Evidence And Never Automatically Replays An Interrupted Callback")]
    [InlineData("accepted")]
    [InlineData("begun")]
    public async Task RequiresStoppedWriter(string phase)
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
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        var store = new ManualWorkStore(instance, database, lease, resources, new(), TimeProvider.System);
        Assert.True(store.ReconcilePriorOwners());
        var recovered = store.Lookup(operation, "fixture:caller", "fixture", "tasks", task, 7)!;
        Assert.Equal(phase == "accepted" ? "notStarted" : "unknown", recovered.Result!.Completion);
        Assert.Null(recovered.Result.Returned); Assert.Null(recovered.Result.FinalConsumedMilliseconds);
        var original = recovered.ResultReference;
        Assert.True(store.ReconcilePriorOwners());
        Assert.Equal(original, store.Lookup(operation, "fixture:caller", "fixture", "tasks", task, 7)!.ResultReference);
        _ = StateDatabase.OpenExisting(instance, Path.Combine(root, "reconciled-startup"), new JournalStartupAdmission(instance, new()));
    }
}
