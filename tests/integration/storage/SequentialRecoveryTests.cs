using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class SequentialRecoveryTests
{
    [Fact(DisplayName = "Killing The Real Owner After One Stopped Step Recovers Its Evidence And The Unallocated Frozen Suffix")]
    public async Task RecoversCollectedSuffix()
    {
        using var fixture = new ExecutionSequentialFixture(accept: false); var source = fixture.Source;
        var identity = PhysicalObjectIdentity.Read(source.Configuration); var original = File.ReadAllBytes(source.Configuration);
        var input = Path.Combine(source.Root, "sequential-crash-input.json"); var reportPath = Path.Combine(source.Root, "sequential-crash-report.json");
        var operation = Guid.NewGuid();
        File.WriteAllBytes(input, JsonSerializer.SerializeToUtf8Bytes(new
        {
            runtimeRoot = source.Instance.RuntimeRoot, point = "stopped-observed", queueId = source.QueueId,
            catalogRevision = source.Definitions.Read().Revision, settingsRevision = source.Settings.Read().Revision,
            operationId = operation, subject = ExecutionSequentialFixture.Subject
        }));
        source.Lease.Dispose();
        using var owner = new OwnedCrashProcess();
        var root = owner.Start(["execution-terminal-recovery", input, reportPath], source.Root, source.Instance.InstanceId, operation,
            Path.Combine(source.Root, "sequential-owner.json"));
        var report = await OwnedCrashProcess.ReadReportAsync(reportPath);
        Assert.Equal("faultReady", report.GetProperty("status").GetString()); Assert.Equal(root.ProcessId, report.GetProperty("ownerProcessId").GetInt32());
        var parentId = report.GetProperty("journalId").GetGuid(); var binding = report.GetProperty("binding").Deserialize<StepEvidenceBinding>()!;
        var stopped = await owner.StopAsync(); Assert.True(stopped.CompleteJobStopped); Assert.Equal(0u, stopped.Accounting.ActiveProcesses);
        Assert.All(stopped.Family, member => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(member)));
        var instance = new InstanceRoot(new()).OpenExisting(source.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenExisting(instance, Path.Combine(source.Root, "sequential-restart-diagnostics"), new JournalStartupAdmission(instance, new()));
        var index = new StartupJournalRecovery(instance, new(), database,
            new(Path.Combine(source.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
        Assert.True(index.Current.IsReady);
        var state = AttemptWriterState.Read(instance, database, new(), parentId);
        Assert.Equal(JournalPhase.Committed, state.Journal.Phase); Assert.Equal(state.Journal.Revision, state.Latest.Revision);
        Assert.Single(state.Steps); Assert.Equal(3, state.Coverage.Length);
        Assert.Equal(StepCompletionKind.Collected, state.Coverage[0].Kind);
        Assert.All(state.Coverage.Skip(1), item => { Assert.Equal(StepCompletionKind.NotDispatched, item.Kind); Assert.Null(item.Result); });
        var receipt = new AttemptReceiptStore(database).Read(binding.AttemptId)!;
        Assert.Equal(EngineOutcome.Failed, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.Unverified, receipt.Facts.Business);
        Assert.Equal(LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle); Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        var result = Assert.Single(StepResultCodec.Read(state.Latest.Root, state.Total));
        Assert.Equal(EngineOutcome.Failed, new StepEvidenceReader(instance, database, new()).Read(binding, result.Latest.Artifact, true).Engine);
        Assert.DoesNotContain(new RecoveryJournalRegistry(database).ReadSnapshot().Isolations, item => item.JournalId == parentId);
        Assert.Equal(identity, PhysicalObjectIdentity.Read(source.Configuration)); Assert.Equal(original, File.ReadAllBytes(source.Configuration));
    }
}
