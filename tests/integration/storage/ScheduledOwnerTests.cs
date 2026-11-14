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

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ScheduledOwnerTests
{
    [Fact(DisplayName = "A Real Scheduled Callback Owner Killed After Begin Is Closed Unknown Once Without Replaying Or Rebuilding Its Counter")]
    public async Task KeepsUnknownCallback()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "scheduled-callback-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var reportPath = Path.Combine(root, "callback.json"); var effectPath = Path.Combine(root, "effect.txt");
        using var owner = new OwnedCrashProcess();
        var actual = owner.Start(["notification-scheduled-recovery", root, reportPath, effectPath], root,
            Guid.NewGuid(), Guid.NewGuid(), Path.Combine(root, "native-root.json"));
        var report = await OwnedCrashProcess.ReadReportAsync(reportPath, TimeSpan.FromSeconds(30));
        Assert.Equal("callbackRunning", report.GetProperty("status").GetString());
        Assert.Equal(actual.ProcessId, report.GetProperty("processId").GetInt32());
        Assert.True(report.GetProperty("liveReconcileRejected").GetBoolean());
        Assert.NotEqual(WriterState.Stopped, WriterStopEvidence.ObserveProcess(new(actual.ProcessId, actual.CreatedAtUtc, actual.ImagePath, actual.ImageSha256, actual.UserSid, actual.IsElevated)));
        var instance = report.GetProperty("instance").Deserialize<RuntimeInstance>()!;
        var occurrence = Guid.ParseExact(report.GetProperty("occurrenceId").GetString()!, "D");
        var effectBefore = File.ReadAllBytes(effectPath);
        Assert.ThrowsAny<IOException>(() => { using var forbidden = InstanceLease.Acquire(instance, new()); });
        var stopped = await owner.StopAsync(); Assert.True(stopped.CompleteJobStopped);
        Assert.Contains(stopped.Family, item => item.ProcessId == actual.ProcessId && item.CreatedAtUtc == actual.CreatedAtUtc);
        Assert.All(stopped.Family, item => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(item)));
        _ = await owner.AwaitExitAsync();

        using var lease = InstanceLease.Acquire(instance, new());
        var roots = new PayloadApplyRoots(Path.Combine(root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"));
        var database = StateDatabase.OpenExisting(instance, Path.Combine(root, "inspection"), new JournalStartupAdmission(instance, new(), roots));
        var index = new StartupJournalRecovery(instance, new(), database, roots).Recover(lease);
        var lifecycle = new HostLifecycle(index, TimeProvider.System); var resources = new ResourceLeaseManager(lifecycle);
        var before = Read(instance, database); Assert.Null(before.Result); Assert.NotNull(before.Begin);
        Assert.Equal(occurrence, before.Source.Document.OperationId);
        Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(before.Source.Document.Creator.Process));
        var execution = new ExecutionAdmissionService(instance, database, new(), lease, resources, index, new(), new(5, 6, 7));
        var service = new PersistentScheduleService(instance, database, lease, resources, lifecycle, index, execution);
        var after = Read(instance, database); Assert.Equal("unknown", after.Result!.Completion);
        Assert.Equal("scheduled_provider_owner_lost", after.Result.ReasonCode); Assert.Null(after.Result.FinalConsumedMilliseconds);
        Assert.Equal(before.Source.Reference, after.Source.Reference); Assert.Equal(before.BeginReference, after.BeginReference);
        Assert.Equal(before.Begin, after.Begin); Assert.NotNull(after.CloseReference); Assert.NotNull(after.ResultReference);
        service.Start(); await service.StopAsync();
        Assert.Equal(effectBefore, File.ReadAllBytes(effectPath));
        var replay = new ScheduledProviderPublication(instance, database, new(), lease, resources, TimeProvider.System);
        Assert.True(replay.ReconcilePriorOwners());
        var repeated = Read(instance, database);
        Assert.Equal(after.Source.Reference, repeated.Source.Reference); Assert.Equal(after.BeginReference, repeated.BeginReference);
        Assert.Equal(after.CloseReference, repeated.CloseReference); Assert.Equal(after.ResultReference, repeated.ResultReference);
        Assert.Equal(after.Result, repeated.Result);
        _ = StateDatabase.OpenExisting(instance, Path.Combine(root, "second-inspection"), new JournalStartupAdmission(instance, new(), roots));
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.All); using var count = connection.CreateCommand();
        count.CommandText = "SELECT (SELECT COUNT(*) FROM main.scheduled_provider_pending)+(SELECT COUNT(*) FROM records.attempt_receipts)+(SELECT COUNT(*) FROM notifications.notification_deliveries);";
        Assert.Equal(0L, count.ExecuteScalar());
    }

    private static ScheduledProviderState Read(RuntimeInstance instance, StateDatabase database)
    {
        var states = new List<ScheduledProviderState>(); ScheduledProviderInventory.Visit(instance, database, new(), states.Add);
        return Assert.Single(states);
    }
}
