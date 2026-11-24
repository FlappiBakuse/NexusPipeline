using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class HooksRecoveryTests
{
    [Fact(DisplayName = "Recovery Cannot Seal Or Restore A Stopped Hook Scene When Its Actual Original Backup Was Changed")]
    public async Task RejectsChangedBackups()
    {
        using var fixture = new ExecutionHooksFixture(configuration: true, accept: false); var source = fixture.Source;
        var request = source.Request(new BindingTarget(source.BindingA)); var operation = Guid.NewGuid();
        var input = Path.Combine(source.Root, "hooks-crash-input.json"); var reportPath = Path.Combine(source.Root, "hooks-crash-report.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { runtimeRoot = source.Instance.RuntimeRoot, point = "hook-result-ahead",
            bindingId = source.BindingA, catalogRevision = request.ExpectedCatalogRevision, settingsRevision = request.ExpectedSettingsRevision,
            operationId = operation, subject = ExecutionHooksFixture.Subject }));
        source.Lease.Dispose();
        using var owner = new OwnedCrashProcess();
        owner.Start(["execution-hooks-recovery", input, reportPath], source.Root, source.Instance.InstanceId, operation,
            Path.Combine(source.Root, "hooks-owner.json"));
        var report = await OwnedCrashProcess.ReadReportAsync(reportPath, TimeSpan.FromMinutes(3));
        Assert.True(report.GetProperty("status").GetString() == "faultReady", report.GetRawText());
        var stopped = await owner.StopAsync(); Assert.True(stopped.CompleteJobStopped);
        Assert.All(stopped.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        var workspace = Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(source.Configuration)!, ".nexus-*"));
        var backup = Path.Combine(workspace, "before");
        File.AppendAllText(backup, "\nfixture changed backup");
        var backupBytes = File.ReadAllBytes(backup); var backupIdentity = PhysicalObjectIdentity.Read(backup);
        var installed = File.ReadAllBytes(source.Configuration); var installedIdentity = PhysicalObjectIdentity.Read(source.Configuration);
        var instance = new InstanceRoot(new()).OpenExisting(source.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenExisting(instance, Path.Combine(source.Root, "restart-diagnostics"),
            new JournalStartupAdmission(instance, new()), lease);
        var index = new StartupJournalRecovery(instance, new(), database,
            new(Path.Combine(source.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
        Assert.Equal(installed, File.ReadAllBytes(source.Configuration));
        Assert.Equal(installedIdentity, PhysicalObjectIdentity.Read(source.Configuration));
        Assert.Equal(backupBytes, File.ReadAllBytes(backup)); Assert.Equal(backupIdentity, PhysicalObjectIdentity.Read(backup));
        var registry = new RecoveryJournalRegistry(database);
        var child = Assert.Single(registry.ReadSnapshot().Journals, item => item.Kind == JournalKind.ConfigExchange);
        Assert.Equal(JournalPhase.Quarantined, child.Phase);
        Assert.Contains(index.Current.Isolations, item => item.JournalId == child.JournalId &&
            item.ResourceKey.StartsWith("configuration:", StringComparison.Ordinal) && item.ReasonCode == "recovery_quarantined");
        var physical = new JournalStore(instance, new()).Read(child.JournalId);
        Assert.Equal(child.Phase, physical.Phase);
        Assert.Equal(child.Revision, physical.Revision);
        Assert.Equal(1, physical.Root.GetProperty("participants").GetArrayLength());
        Assert.Equal(JournalPhase.Applied, registry.ReadJournal(report.GetProperty("journalId").GetGuid()).Phase);
        Assert.Null(new AttemptReceiptStore(database).Read(report.GetProperty("attemptId").GetGuid()));
    }

    [Theory(DisplayName = "Actual Owner Kill Replays Hook Facts And Restores The Installed Group Without Inventing A Main Clock")]
    [InlineData("collected")]
    [InlineData("hook-result-ahead")]
    public async Task RestoresKilledHooks(string point)
    {
        using var fixture = new ExecutionHooksFixture(configuration: true, accept: false); var source = fixture.Source;
        var original = PhysicalObjectIdentity.Read(source.Configuration); var bytes = File.ReadAllBytes(source.Configuration);
        var request = source.Request(new BindingTarget(source.BindingA)); var operation = Guid.NewGuid();
        var input = Path.Combine(source.Root, "hooks-crash-input.json"); var reportPath = Path.Combine(source.Root, "hooks-crash-report.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { runtimeRoot = source.Instance.RuntimeRoot, point,
            bindingId = source.BindingA, catalogRevision = request.ExpectedCatalogRevision, settingsRevision = request.ExpectedSettingsRevision,
            operationId = operation, subject = ExecutionHooksFixture.Subject }));
        source.Lease.Dispose();
        using var owner = new OwnedCrashProcess();
        var actual = owner.Start(["execution-hooks-recovery", input, reportPath], source.Root, source.Instance.InstanceId, operation,
            Path.Combine(source.Root, "hooks-owner.json"));
        var report = await OwnedCrashProcess.ReadReportAsync(reportPath, TimeSpan.FromMinutes(3));
        Assert.True(report.GetProperty("status").GetString() == "faultReady", report.GetRawText());
        Assert.Equal(actual.ProcessId, report.GetProperty("ownerProcessId").GetInt32());
        Assert.Equal(point, report.GetProperty("point").GetString());
        var beforeRecovery = report.GetProperty("hookRoles").EnumerateArray().Select(role => role.GetString()).Order(StringComparer.Ordinal);
        Assert.Equal(point == "collected" ? new[] { "postHook", "preHook" } : new[] { "preHook" }, beforeRecovery);
        if (point == "hook-result-ahead") Assert.True(report.GetProperty("physicalRevision").GetInt64() > report.GetProperty("sqlRevision").GetInt64());
        var parent = report.GetProperty("journalId").GetGuid(); var attempt = report.GetProperty("attemptId").GetGuid();
        var killed = await owner.StopAsync();
        Assert.True(killed.CompleteJobStopped); Assert.Equal(0u, killed.Accounting.ActiveProcesses);
        Assert.All(killed.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));

        var instance = new InstanceRoot(new()).OpenExisting(source.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenExisting(instance, Path.Combine(source.Root, "restart-diagnostics"), new JournalStartupAdmission(instance, new()));
        var index = new StartupJournalRecovery(instance, new(), database,
            new(Path.Combine(source.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        new RecoveryCompletionService(instance, database, new(), lease, resources, index).ReconcilePending();
        var state = AttemptWriterState.Read(instance, database, new(), parent);
        var receipt = Assert.IsType<AttemptReceipt>(new AttemptReceiptStore(database).Read(attempt));
        Assert.Equal(JournalPhase.Committed, state.Journal.Phase); Assert.Equal(state.Journal.Revision, state.Latest.Revision);
        Assert.Equal(LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle); Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(EngineOutcome.Succeeded, receipt.Facts.Engine);
        Assert.Equal(point == "collected" ? BusinessOutcome.Succeeded : BusinessOutcome.Unverified, receipt.Facts.Business);
        Assert.Equal(point == "collected" ? 3 : 1, state.Jobs.Length);
        Assert.Equal(point == "collected" ? 1 : 0, state.Steps.Length);
        var closureStore = new JobClosureStore(instance, database, new());
        foreach (var job in state.Jobs)
        {
            var closure = Assert.IsType<FrozenJobClosure>(closureStore.ReadForExecutionEvidence(parent, job.WriterId, job.JobName, job.SessionId));
            Assert.True(closure.WasExecuted); Assert.NotNull(closure.Root);
            Assert.Equal(job.ClosedElapsedMilliseconds, closure.RunElapsedMilliseconds);
            Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        }
        var reader = new HookEvidenceReader(instance, database, new());
        foreach (var hook in HookResultCodec.Read(state.Latest.Root, state.Total))
            Assert.All(reader.ReadReferences(hook.Binding, hook.Role, hook.Artifact),
                reference => { using var held = new ImmutableBlobStore(instance, new()).OpenVerified(reference); Assert.Equal(reference.SizeBytes, held.Length); });
        Assert.Equal(bytes, File.ReadAllBytes(source.Configuration)); Assert.Equal(original, PhysicalObjectIdentity.Read(source.Configuration));
        Assert.True(index.Current.IsReady);
        Assert.DoesNotContain(new RecoveryJournalRegistry(database).ReadSnapshot().Isolations, isolation => isolation.JournalId == parent);
        if (point == "hook-result-ahead")
        {
            var suppressed = Assert.Single(BindingSuppressionCodec.Read(state.Latest.Root, state.Total));
            Assert.Contains("process", suppressed.Roles); Assert.NotNull(suppressed.DispatchOperationId);
        }
    }
}
