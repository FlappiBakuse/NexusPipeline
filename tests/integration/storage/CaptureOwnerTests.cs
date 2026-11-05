using System.Runtime.Versioning;
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
public sealed class CaptureOwnerTests
{
    [Theory(DisplayName = "Actual Capture Owner Exit Preserves The Original Window And Never Invents A Replacement Frame Or Necessary Closure")]
    [InlineData("CaptureSourcePublished", false, false)]
    [InlineData("CaptureRootPublished", false, false)]
    [InlineData("CaptureParentStopped", true, false)]
    [InlineData("CaptureCollectedAppended", true, true)]
    public async Task PreservesOwnerCapture(
        string point, bool necessaryClosed, bool captured)
    {
        using var fixture = new PcCrashFixture(nativeWindow: true);
        await fixture.StartAsync(point);
        var source = fixture.Source;
        var before = AttemptWriterState.Read(source.Instance, source.Database, new(), fixture.Parent);
        var original = ReadCapture(source.Instance, source.Database, before);
        if (captured)
        {
            Assert.Equal(before.Journal.Revision + 1, before.Latest.Revision);
            Assert.Equal("captured", original.Result!.Outcome);
        }
        else
        {
            Assert.Null(original.Result);
            Assert.Equal(before.Journal.Revision, before.Latest.Revision);
        }
        Assert.Equal(point != "CaptureSourcePublished", original.Root is not null);
        byte[]? png = original.Result?.Png is { } image ? File.ReadAllBytes(ContentPath(source.Instance, image)) : null;
        if (necessaryClosed)
        {
            var job = Assert.Single(before.Jobs);
            Assert.NotNull(new JobClosureStore(source.Instance, source.Database, new())
                .ReadForExecutionEvidence(fixture.Parent, job.WriterId, job.JobName, job.SessionId));
        }
        else
        {
            Assert.Null(original.Parent);
            Assert.All(before.Jobs, job => Assert.Null(new JobClosureStore(source.Instance, source.Database, new())
                .ReadForExecutionEvidence(fixture.Parent, job.WriterId, job.JobName, job.SessionId)));
        }
        await fixture.KillOwnerAsync();
        await fixture.RequireStoppedAsync();
        if (original.Root is { } root)
            Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(root.NativeRoot));
        var instance = new InstanceRoot(new()).OpenExisting(source.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(instance, new());
        if (!necessaryClosed)
        {
            var installed = File.ReadAllBytes(source.Configuration);
            var identity = PhysicalObjectIdentity.Read(source.Configuration);
            var refusal = Assert.Throws<IOException>(() => StateDatabase.OpenExisting(instance,
                Path.Combine(source.Root, "capture-restart-diagnostics"), new JournalStartupAdmission(instance, new())));
            Assert.Equal("startup_journal_writer_stop_unconfirmed", refusal.Message);
            var unchanged = AttemptWriterState.Read(instance, source.Database, new(), fixture.Parent);
            Assert.Equal(before.Latest.Export(), unchanged.Latest.Export());
            Assert.Equal(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(before.Journal),
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(unchanged.Journal));
            Assert.Null(new AttemptReceiptStore(source.Database).Read(fixture.Attempt));
            Assert.Equal(installed, File.ReadAllBytes(source.Configuration));
            Assert.Equal(identity, PhysicalObjectIdentity.Read(source.Configuration));
            Assert.NotEqual(fixture.OriginalBytes, installed);
            var unavailable = ReadCapture(instance, source.Database, unchanged);
            Assert.Equal(original.State, unavailable.State); Assert.Null(unavailable.Result);
            return;
        }
        var database = StateDatabase.OpenExisting(instance, Path.Combine(source.Root, "capture-restart-diagnostics"),
            new JournalStartupAdmission(instance, new()));
        var index = new StartupJournalRecovery(instance, new(), database,
            new(Path.Combine(source.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        var completion = new RecoveryCompletionService(instance, database, new(), lease, resources, index);
        completion.ReconcilePending();
        var after = AttemptWriterState.Read(instance, database, new(), fixture.Parent);
        var saved = ReadCapture(instance, database, after);
        Assert.Equal(original.State.Binding, saved.State.Binding);
        Assert.Equal(original.State.Source, saved.State.Source); Assert.Equal(original.State.Root, saved.State.Root);
        Assert.Equal(after.Journal.Revision, after.Latest.Revision);
        Assert.True(index.Current.IsReady);
        Assert.Equal(JournalPhase.Committed, after.Journal.Phase);
        var receipt = Assert.IsType<AttemptReceipt>(new AttemptReceiptStore(database).Read(fixture.Attempt));
        Assert.Equal(LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle);
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(fixture.OriginalBytes, File.ReadAllBytes(source.Configuration));
        Assert.Equal(fixture.OriginalIdentity, PhysicalObjectIdentity.Read(source.Configuration));
        Assert.NotNull(saved.Parent); Assert.NotNull(saved.Result);
        if (captured)
        {
            Assert.Equal(original.State.Result, saved.State.Result);
            Assert.Equal("captured", saved.Result!.Outcome); Assert.Equal(original.Result!.Png, saved.Result.Png);
            using var opened = new CaptureEvidenceReader(instance, database, new())
                .OpenCaptured(saved.State.Binding, saved.State.Result!);
            using var bytes = new MemoryStream(); opened.CopyTo(bytes);
            Assert.Equal(png, bytes.ToArray());
        }
        else
        {
            Assert.Equal("unavailable", saved.Result!.Outcome);
            Assert.Equal("execution_capture_recovered_unavailable", saved.Result.ReasonCode);
            Assert.Null(saved.Result.Png); Assert.Null(saved.Result.HelperStopped);
        }
        var stable = after.Latest.Export();
        completion.ReconcilePending();
        Assert.Equal(stable, AttemptWriterState.Read(instance, database, new(), fixture.Parent).Latest.Export());
        var final = ReadCapture(instance, database, after);
        Assert.Equal(saved.State, final.State);
        Assert.Single(ExecutionCaptureCodec.Read(after.Latest.Root, after.Total, after.Attempt.ExecutionPlan.Sha256));
    }

    private static CaptureSavedEvidence ReadCapture(RuntimeInstance instance, StateDatabase database,
        AttemptWriterState state) => Assert.Single(new CaptureEvidenceReader(instance, database, new())
            .ReadMetadata(state.Journal, state.Attempt, state.Total,
                new JournalStore(instance, new()).ReadAllRevisions(state.Journal.JournalId)));

    private static string ContentPath(RuntimeInstance instance, BlobReference reference) =>
        Path.Combine(instance.RuntimeRoot, reference.RelativePath.Replace('/', Path.DirectorySeparatorChar));
}
