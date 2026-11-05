using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Records;
using NexusPipeline.Platform.Storage.Notifications;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class TerminalRecoveryTests
{
    [Theory(DisplayName = "A Stopped Real Writer Retains Its Known Engine And Raw Evidence Across Owner Kill And Terminal Reconciliation")]
    [InlineData("stopped-observed")]
    [InlineData("applied")]
    [InlineData("terminal-ahead")]
    public async Task PreservesCollectedFacts(string point)
    {
        using var source = new PlanBuilderFixture();
        var originalIdentity = PhysicalObjectIdentity.Read(source.Configuration); var originalBytes = File.ReadAllBytes(source.Configuration);
        var catalog = source.Definitions.Read(); var script = catalog.Scripts.Single(item => item.Id == source.OtherScriptId);
        var root = catalog.Roots.Single(item => item.Id == script.RootId);
        var executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        var marker = Path.Combine(source.Root, "terminal-child");
        var changes = new AutomationChanges(catalog.Revision, [root with { Path = Path.GetDirectoryName(executable)! }],
            [script with { Executable = new(root.Id, Path.GetFileName(executable)), Arguments = ["process-output", "tail", marker] }], [], [], [], [], []);
        source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "terminal-crash", changes), changes, PlanBuilderFixture.CreatedAt);
        var request = source.Request(new BindingTarget(source.OtherBinding)); var operationId = Guid.NewGuid();
        var input = Path.Combine(source.Root, "terminal-input.json"); var reportPath = Path.Combine(source.Root, "terminal-report.json");
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new { runtimeRoot = source.Instance.RuntimeRoot, point,
            bindingId = source.OtherBinding, catalogRevision = request.ExpectedCatalogRevision, settingsRevision = request.ExpectedSettingsRevision,
            operationId, subject = "terminal-crash" }));
        source.Lease.Dispose();
        using var owner = new OwnedCrashProcess();
        var owned = owner.Start(["execution-terminal-recovery", input, reportPath], source.Root, source.Instance.InstanceId, operationId,
            Path.Combine(source.Root, "terminal-owner.json"));
        var report = await OwnedCrashProcess.ReadReportAsync(reportPath);
        Assert.Equal("faultReady", report.GetProperty("status").GetString()); Assert.Equal(owned.ProcessId, report.GetProperty("ownerProcessId").GetInt32());
        var journalId = report.GetProperty("journalId").GetGuid();
        var binding = report.GetProperty("binding").Deserialize<StepEvidenceBinding>()!;
        var closure = report.GetProperty("closure").Deserialize<FrozenJobClosure>()!;
        Assert.True(closure.WasExecuted); Assert.NotNull(closure.Root); Assert.NotEmpty(closure.Family);
        Assert.Contains(closure.Family, member => member == closure.Root);
        Assert.Equal(closure.Family.Length, closure.Family.DistinctBy(member => (member.ProcessId, member.CreatedAtUtc)).Count());
        Assert.All(closure.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        if (point == "terminal-ahead") Assert.True(report.GetProperty("physicalRevision").GetInt64() > report.GetProperty("sqlRevision").GetInt64());
        var killed = await owner.StopAsync(); Assert.True(killed.CompleteJobStopped); Assert.Equal(0u, killed.Accounting.ActiveProcesses);
        Assert.All(killed.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));

        var instance = new InstanceRoot(new()).OpenExisting(source.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenExisting(instance, Path.Combine(source.Root, "restart-diagnostics"), new JournalStartupAdmission(instance, new()));
        var index = new StartupJournalRecovery(instance, new(), database,
            new(Path.Combine(source.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        new RecoveryCompletionService(instance, database, new(), lease, resources, index).ReconcilePending();
        var state = AttemptWriterState.Read(instance, database, new(), journalId);
        Assert.Equal(JournalPhase.Committed, state.Journal.Phase); Assert.Equal(state.Journal.Revision, state.Latest.Revision);
        Assert.Equal(EffectGateState.Entered, state.Gate.State);
        var receipt = Assert.IsType<AttemptReceipt>(new AttemptReceiptStore(database).Read(binding.AttemptId));
        Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
        Assert.Equal(point == "terminal-ahead" ? LifecycleOutcome.Completed : LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle);
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        var finality = new ExecutionRunFinality(instance, database, new());
        Assert.False(finality.IsFinal(receipt));
        var record = Assert.Single(new RunRecordStore(database).ReadPage(new()).Items);
        Assert.Equal(receipt.Facts, record.FinalAttempt);
        Assert.Equal(EndTimeKind.ReconciledTerminalObservation, record.EndTimeKind);
        Assert.Equal(record.DurationMilliseconds.HasValue ? RecordDurationKind.CheckpointLowerBound : RecordDurationKind.Unknown, record.DurationKind);
        new ExecutionRecordProjection(instance, database, new(), lease, finality).ReconcileStopped(receipt);
        Assert.Equal(record, Assert.Single(new RunRecordStore(database).ReadPage(new()).Items));
        var notifications = new ExecutionNotificationRegistration(instance, database, new(), lease, finality);
        notifications.OnCommitted(receipt);
        Assert.Equal(0, notifications.ReconcilePending());
        Assert.False(finality.IsFinal(receipt));
        Assert.Throws<IOException>(() => new ExecutionRecordProjection(instance, database, new(), lease, finality)
            .ReconcileStopped(receipt with { Facts = receipt.Facts with { ReasonCode = "forged_recovery" } }));
        Assert.DoesNotContain(new RecoveryJournalRegistry(database).ReadSnapshot().Isolations, item => item.JournalId == journalId);
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total)); Assert.True(result.IsCollected);
        var reader = new StepEvidenceReader(instance, database, new());
        Assert.Equal(EngineOutcome.Failed, reader.Read(binding, result.Latest.Artifact, true).Engine);
        var references = StepResultCodec.ReadAll(state.Latest.Root).SelectMany(item => reader.ReadReferences(binding, item.Artifact)).Distinct().ToArray();
        Assert.NotEmpty(references);
        var blobs = new ImmutableBlobStore(instance, new());
        Assert.All(references, reference => { using var content = blobs.OpenVerified(reference); Assert.Equal(reference.SizeBytes, content.Length); });
        var persisted = new JobClosureStore(instance, database, new()).ReadForExecutionEvidence(journalId, binding.WriterId, binding.JobName, binding.SessionId);
        Assert.NotNull(persisted); Assert.Equal(closure.Root, persisted.Root); Assert.True(closure.Family.SequenceEqual(persisted.Family));
        Assert.Equal(originalIdentity, PhysicalObjectIdentity.Read(source.Configuration)); Assert.Equal(originalBytes, File.ReadAllBytes(source.Configuration));
        var before = Directory.GetFiles(Path.Combine(instance.RuntimeRoot, "journals", journalId.ToString("D")))
            .Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToArray();
        new StartupJournalRecovery(instance, new(), database,
            new(Path.Combine(source.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
        Assert.Equal(receipt, new AttemptReceiptStore(database).Read(binding.AttemptId));
        Assert.Equal(before, Directory.GetFiles(Path.Combine(instance.RuntimeRoot, "journals", journalId.ToString("D")))
            .Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToArray());
    }
}
