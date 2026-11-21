using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ExecutionRestorationTests
{
    [Fact(DisplayName = "Accepted Cancellation Reopens A Sealed Configuration After Its First Restore Projection Fails")]
    public async Task ResumesRestorePublication()
    {
        var fault = new RestorePublicationFault();
        using var fixture = new ExecutionRuntimeFixture(configuration: true, mode: "wait", restorationObserver: fault);
        var source = fixture.Source;
        var original = PhysicalObjectIdentity.Read(source.Configuration);
        var bytes = File.ReadAllBytes(source.Configuration);
        var pending = fixture.Consumer.RunNextAsync();
        await fixture.Ready(".output-ready");
        File.WriteAllText(source.Configuration, "{\"fixture\":false}\n");
        fixture.Cancel();

        var receipt = Assert.IsType<AttemptReceipt>(await pending);
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(receipt, new AttemptReceiptStore(source.Database).Read(receipt.Facts.AttemptId));
        Assert.NotEqual(Guid.Empty, fault.JournalId);
        var child = new JournalStore(source.Instance, new()).Read(fault.JournalId);
        Assert.Equal(JournalPhase.Committed, child.Phase);
        Assert.Single(child.Root.GetProperty("receipts").EnumerateArray(), item =>
            item.GetProperty("stepId").GetString() == "retire-candidate" && item.GetProperty("effect").GetString() == "started");
        Assert.Contains(new RunDiagnosticStore(source.Instance, source.Database).Read([fixture.Accepted.JournalId]),
            item => item.ReasonCode == "configuration_restore_retry" && item.Phase == "configuration-publication" && item.ResourceJournalId == fault.JournalId);
        Assert.Equal(original, PhysicalObjectIdentity.Read(source.Configuration));
        Assert.Equal(bytes, File.ReadAllBytes(source.Configuration));
        Assert.Equal(0, fixture.Consumer.PendingReconciliationCount);
        Assert.Empty(fixture.Consumer.ReadActive(ExecutionSequentialFixture.Subject));
        Assert.Equal(0, fixture.Base.Count("resource_isolations"));
        var adjacent = Path.Combine(Path.GetDirectoryName(source.Configuration)!, $".nexus-{fault.JournalId:D}");
        Assert.True(Directory.Exists(adjacent));
        var cleanup = new ConfigurationWorkspaceCleanup(source.Instance, source.Database, new(), source.Lease, fixture.Base.Resources);
        var console = Console.Error;
        using var profileOutput = new StringWriter();
        try
        {
            Console.SetError(profileOutput);
            using var profile = StartupProfile.Begin("cleanup-inventory-test");
            Assert.True(await cleanup.SweepAsync(default));
            source.Database.Write((connection, transaction) => AutomationRowWriter.Row(connection, transaction,
                "UPDATE main.scheduling_watermark SET revision=revision+1 WHERE singleton=1;"));
            Assert.True(await cleanup.SweepAsync(default));
        }
        finally { Console.SetError(console); }
        using var measured = JsonDocument.Parse(profileOutput.ToString().Split('\n').Single(line =>
            line.Contains("\"phase\":\"cleanup-inventory-test\"", StringComparison.Ordinal)));
        Assert.Equal(1, measured.RootElement.GetProperty("steps").EnumerateArray().Single(step =>
            step.GetProperty("name").GetString() == "configuration.cleanup.journal").GetProperty("count").GetInt32());
        Assert.False(Directory.Exists(adjacent));
        Assert.True(cleanup.TryClean(fault.JournalId));
        Assert.Equal(original, PhysicalObjectIdentity.Read(source.Configuration));
        var next = fixture.Base.Service.Start(Guid.NewGuid(), ExecutionSequentialFixture.Subject,
            source.Request(new QueueTarget(source.QueueId)), DateTimeOffset.UtcNow);
        Assert.NotEqual(fixture.Accepted.JournalId, next.JournalId);
    }

    [Fact(DisplayName = "An Unverifiable Backup After A Restore Publication Failure Stays Isolated Without Moving Configuration")]
    public async Task IsolatesChangedBackup()
    {
        string? configuration = null;
        const string changedOriginal = "{\"backupReplaced\":true}";
        const string candidate = "{\"fixture\":false}";
        var fault = new RestorePublicationFault(journal =>
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(configuration!)!, $".nexus-{journal:D}", "before"), changedOriginal));
        using var fixture = new ExecutionRuntimeFixture(configuration: true, mode: "wait", restorationObserver: fault);
        var source = fixture.Source;
        configuration = source.Configuration;
        var pending = fixture.Consumer.RunNextAsync();
        await fixture.Ready(".output-ready");
        File.WriteAllText(configuration, candidate);
        fixture.Cancel();

        var failure = await Record.ExceptionAsync(async () => { _ = await pending; });
        var primary = Assert.IsType<ConfigurationRestorationFailure>(failure is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions[0] : failure);
        var errors = Assert.IsType<AggregateException>(primary.InnerException).InnerExceptions;
        Assert.Collection(errors, error => Assert.Equal("fixture_restore_projection_sharing_violation", error.Message),
            error => Assert.Equal("configuration_restoration_unverified", error.Message));
        Assert.Equal(JournalPhase.Quarantined, fixture.State.Journal.Phase);
        Assert.Null(new AttemptReceiptStore(source.Database).Read(Guid.Parse(fixture.State.Attempt.AttemptId)));
        Assert.Equal(candidate, File.ReadAllText(configuration));
        Assert.Equal(changedOriginal, File.ReadAllText(Path.Combine(Path.GetDirectoryName(configuration)!, $".nexus-{fault.JournalId:D}", "before")));
        Assert.Equal(1, fixture.Consumer.PendingReconciliationCount);
        Assert.Equal("reconciliationRequired", Assert.Single(fixture.Consumer.ReadActive(ExecutionSequentialFixture.Subject)).Phase);
        Assert.True(fixture.Base.Index.Current.IsReady);
        Assert.All(fixture.Base.Index.Current.Isolations, isolation => Assert.True(isolation.CanOrdinaryExit));
        Assert.DoesNotContain(fixture.Base.Index.Current.Isolations, isolation => isolation.ResourceKey is "settings:host" or "automation:definitions");
        Assert.NotNull(fixture.Base.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).Permit);
        Assert.Collection(new RunDiagnosticStore(source.Instance, source.Database).Read([fixture.Accepted.JournalId])
                .Where(item => item.ResourceJournalId == fault.JournalId),
            item => Assert.Equal("configuration-publication", item.Phase), item => Assert.Equal("configuration-scene", item.Phase));
    }

    [Fact(DisplayName = "Cold Attempt Recovery Keeps The Accepted Cancellation On A Stopped And Restored Run")]
    public async Task PreservesCancellationInRecovery()
    {
        using var fixture = new ExecutionSequentialFixture(configuration: true, mode: "wait", independentSuccessors: true);
        var source = fixture.Source;
        var cleanup = new ConfigurationWorkspaceCleanup(source.Instance, source.Database, new(), source.Lease, fixture.Base.Resources);
        Assert.True(await cleanup.SweepAsync(default));
        var original = PhysicalObjectIdentity.Read(source.Configuration);
        var bytes = File.ReadAllBytes(source.Configuration);
        var first = fixture.Create(0); first.Start();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            while (!File.Exists(fixture.Marker + ".output-ready")) await Task.Delay(10, timeout.Token);
        var observed = fixture.State;
        var admittedJobs = fixture.Base.Count("journal_writer_jobs");
        var cancel = new ExecutionCancellationStore(source.Instance, source.Database, new(), source.Lease);
        var identity = ExecutionCancellationStore.Identity(Guid.NewGuid(), ExecutionSequentialFixture.Subject, fixture.ParentId,
            observed.Journal.OperationId, Guid.Parse(observed.Attempt.RunId), Guid.Parse(observed.Attempt.AttemptId));
        Assert.True(cancel.Cancel(identity, fixture.ParentId, DateTimeOffset.UtcNow).Acceptance.IsNew);
        await first.Stop(); await first.Collect(); await first.FinishConfiguration();
        var recovery = new EnteredAttemptRecovery(new EnteredRecoverySession(source.Instance, source.Database, new(), source.Lease,
            fixture.ParentId, () =>
            {
                var actual = new JournalWriterStore(source.Database).ObserveForCurrentExecutor(fixture.ParentId);
                return new(source.Instance.InstanceId, fixture.ParentId, actual.IsComplete, actual.State);
            }));

        Assert.Equal(JournalRecoveryResult.Restored, recovery.Recover());
        var adjacent = Assert.Single(Directory.EnumerateDirectories(Path.GetDirectoryName(source.Configuration)!, ".nexus-*"));
        Assert.True(await cleanup.SweepAsync(default));
        Assert.False(Directory.Exists(adjacent));
        var receipt = Assert.IsType<AttemptReceipt>(new AttemptReceiptStore(source.Database).Read(Guid.Parse(observed.Attempt.AttemptId)));
        Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
        Assert.Equal("execution_cancel_requested", receipt.Facts.ReasonCode);
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(JournalRecoveryResult.AlreadyCompleted, recovery.Recover());
        Assert.False(cancel.Cancel(identity, fixture.ParentId, DateTimeOffset.UtcNow).Acceptance.IsNew);
        Assert.Equal(admittedJobs, fixture.Base.Count("journal_writer_jobs"));
        Assert.Single(fixture.State.Jobs);
        Assert.All(fixture.State.Coverage.Skip(1), item => Assert.Equal(StepCompletionKind.NotDispatched, item.Kind));
        Assert.Equal(original, PhysicalObjectIdentity.Read(source.Configuration));
        Assert.Equal(bytes, File.ReadAllBytes(source.Configuration));
    }

    private sealed class RestorePublicationFault(Action<Guid>? changeScene = null) : IConfigurationPersistenceObserver
    {
        internal Guid JournalId { get; private set; }

        public void Reached(ConfigurationPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            if (point != ConfigurationPersistencePoint.JournalFlushed || phase != JournalPhase.Reverting || JournalId != Guid.Empty) return;
            JournalId = journalId;
            changeScene?.Invoke(journalId);
            throw new IOException("fixture_restore_projection_sharing_violation", unchecked((int)0x80070020));
        }
    }
}
