using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class LauncherRecoveryTests
{
    [Theory(DisplayName = "Actual Owner Kill Restores Only The Saved Launcher References Without Inventing An Ending When Its Original Job Name Is Lost")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoresSavedReferences(bool committed)
    {
        await using var fixture = await ProviderCrashFixture.StartAsync(collected: true, launcher: true, committed: committed);
        var launcher = fixture.Launcher!;
        var oldReceipt = new AttemptReceiptStore(fixture.Source.Database).Read(fixture.Attempt);
        await fixture.KillOwnerAsync();
        Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveProcess(fixture.LauncherRoot!));
        await RequireLostAsync(launcher.Binding.JobName);
        var instance = new InstanceRoot(new()).OpenExisting(fixture.Source.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenExisting(instance, Path.Combine(fixture.Source.Root, "launcher-restart-diagnostics"),
            new JournalStartupAdmission(instance, new()));
        var recovery = new StartupJournalRecovery(instance, new(), database,
            new(Path.Combine(fixture.Source.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions")));
        var index = recovery.Recover(lease);
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        new RecoveryCompletionService(instance, database, new(), lease, resources, index).ReconcilePending();
        var registry = new ExecutionProviderRegistry();
        try
        {
            registry.RestoreSavedReferences(instance, database, new(), lease, resources);
            registry.RestoreSavedReferences(instance, database, new(), lease, resources);
            Assert.Equal(1, registry.RetainedCount);
            var state = AttemptWriterState.Read(instance, database, new(), fixture.Parent);
            Assert.Equal(JournalPhase.Committed, state.Journal.Phase);
            var receipt = Assert.IsType<AttemptReceipt>(new AttemptReceiptStore(database).Read(fixture.Attempt));
            Assert.Equal(committed ? LifecycleOutcome.Completed : LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle);
            Assert.Equal(EngineOutcome.Succeeded, receipt.Facts.Engine); Assert.Equal(BusinessOutcome.NotApplicable, receipt.Facts.Business);
            Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
            if (committed) Assert.Equal(oldReceipt, receipt);
            Assert.Equal(fixture.OriginalBytes, File.ReadAllBytes(fixture.Source.Configuration));
            Assert.Equal(fixture.OriginalIdentity, PhysicalObjectIdentity.Read(fixture.Source.Configuration));
            Assert.Null(ProviderLauncherRows.Ended(database, launcher.Binding, launcher.Artifact!));
            Assert.Null(resources.TryAcquire(Guid.NewGuid(), [new("extension:provider-fixture", ResourceClaimMode.Mutation)]).Lease);
            var evidence = new ProviderLauncherEvidence(instance, database, new());
            var retained = evidence.Read<ProviderLauncherRetained>(launcher.Artifact!);
            var intent = evidence.Read<ProviderLauncherRetention>(retained.Intent);
            ProviderLauncherReferences.RequireSubset(state.Total, intent.References, launcher.Binding.ExtensionId);
            var upstreamClaims = PhysicalResourceAdmission.Read(fixture.Source.OtherImage, ResourceClaimMode.Mutation, includeDescendants: false);
            using (var unrelated = resources.TryAcquire(Guid.NewGuid(), upstreamClaims).Lease) Assert.NotNull(unrelated);
            var blobs = new ImmutableBlobStore(instance, new());
            var references = new BlobReferenceIndex(instance, new(), database, blobs);
            var parentPlan = new BlobReference(state.Journal.PlanPath,
                new FileInfo(Path.Combine(instance.RuntimeRoot, state.Journal.PlanPath)).Length, state.Journal.PlanDigest);
            Assert.Contains(retained.Intent, new PlanReferenceClosure(instance, new(), blobs, database).Read(state.Journal, parentPlan));
            var terminal = Assert.Single(ProviderTerminalCodec.Read(state.Latest.Root, state.Total));
            var control = new ProviderControlReader(instance, database, new()).Read(terminal.Binding, terminal.Artifact);
            var raw = Assert.Single(control.Archive.Parts);
            var candidate = Assert.Single(new CleanupPlanCodec(instance, new(), blobs)
                .Capture(Guid.NewGuid(), Guid.NewGuid(), [raw]).Items);
            Assert.Equal(CleanupStepResult.SkippedReferenced, new BlobCleanup(instance, blobs, references).RemoveVerified(candidate));
            using (var process = System.Diagnostics.Process.GetProcessById(fixture.LauncherRoot!.ProcessId))
            {
                File.WriteAllText(fixture.Fixture.LauncherRelease, "release");
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(fixture.LauncherRoot));
            Assert.Null(ProviderLauncherRows.Ended(database, launcher.Binding, launcher.Artifact!));
            Assert.Null(resources.TryAcquire(Guid.NewGuid(), [new("extension:provider-fixture", ResourceClaimMode.Mutation)]).Lease);
            _ = recovery.Recover(lease);
            registry.RestoreSavedReferences(instance, database, new(), lease, resources);
            Assert.Equal(1, registry.RetainedCount);
            Assert.Equal(receipt, new AttemptReceiptStore(database).Read(fixture.Attempt));
            Assert.Equal(state.Latest.Revision, AttemptWriterState.Read(instance, database, new(), fixture.Parent).Latest.Revision);
        }
        finally { await registry.StopMonitorAsync(); }
    }

    private static async Task RequireLostAsync(string name)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            using (var job = JobNative.OpenJobObjectW(4, false, name))
                if (job.IsInvalid) { Assert.Equal(2, Marshal.GetLastPInvokeError()); return; }
            await Task.Delay(20);
        }
        Assert.Fail("The original Native Job name did not disappear after its actual owner exited");
    }
}
