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
public sealed class AndroidOwnerTests
{
    [Theory(DisplayName = "Actual Generic Capture Owner Exit Never Retakes The Image Or Invents Necessary Writer Closure")]
    [InlineData("CaptureSourcePublished", false, false)]
    [InlineData("CaptureRootPublished", false, false)]
    [InlineData("CaptureParentStopped", true, false)]
    [InlineData("CaptureCollectedAppended", true, true)]
    [InlineData("AndroidCollectedPublished", true, true)]
    public Task PreservesExitedCapture(
        string point, bool necessaryClosed, bool captured) => VerifyOwnerAsync(point, necessaryClosed, captured, mumu: false);

    [Theory(DisplayName = "Actual Mu Mu Owner Exit Preserves The Suspended Command Or Recovers Only Its Complete Original Image")]
    [InlineData("CaptureCommandRootPublished", false, false)]
    [InlineData("AndroidCollectedPublished", true, true)]
    public Task RecoversOriginalImage(
        string point, bool necessaryClosed, bool captured) => VerifyOwnerAsync(point, necessaryClosed, captured, mumu: true);

    private static async Task VerifyOwnerAsync(string point, bool necessaryClosed, bool captured, bool mumu)
    {
        using var fixture = new AndroidCrashFixture(capture: true, mumu: mumu);
        await fixture.StartAsync(point);
        var source = fixture.Source;
        var before = AttemptWriterState.Read(source.Instance, source.Database, new(), fixture.Parent);
        var original = ReadCapture(source.Instance, source.Database, before);
        if (mumu) Assert.Equal(captured ? 3 : 1, original.Commands.Length);
        if (captured)
        {
            Assert.Equal(before.Journal.Revision + (point == "CaptureCollectedAppended" ? 1 : 0), before.Latest.Revision);
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
        var commands = fixture.ObservedCommands().Select(path => (Path.GetFileName(path),
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))))).ToArray();
        void RequireNoReplay() => Assert.Equal(commands, fixture.ObservedCommands().Select(path => (Path.GetFileName(path),
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))))).ToArray());
        await fixture.KillOwnerAsync();
        if (original.Root is { } root)
            Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(root.NativeRoot));
        Assert.All(original.Commands, child => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(child.Root.Process)));
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
            RequireNoReplay(); return;
        }
        if (point is "CaptureParentStopped" or "CaptureCollectedAppended")
        {
            var installed = File.ReadAllBytes(source.Configuration);
            var identity = PhysicalObjectIdentity.Read(source.Configuration);
            if (point == "CaptureParentStopped")
            {
                RequireCaptureRefusal(); RequireUnknownControl();
                var blockedRestart = Assert.Throws<IOException>(() => StateDatabase.OpenExisting(instance,
                    Path.Combine(source.Root, "capture-live-recoverer-diagnostics"), new JournalStartupAdmission(instance, new())));
                Assert.Equal("startup_journal_writer_stop_unconfirmed", blockedRestart.Message);
                RequireUnknownControl();
            }
            else
            {
                var isolated = StateDatabase.OpenExisting(instance, Path.Combine(source.Root, "capture-restart-diagnostics"),
                    new JournalStartupAdmission(instance, new()));
                var restart = new StartupJournalRecovery(instance, new(), isolated,
                    new(Path.Combine(source.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions")));
                var blocked = restart.Recover(lease);
                var manager = new ResourceLeaseManager(new HostLifecycle(blocked, TimeProvider.System));
                new RecoveryCompletionService(instance, isolated, new(), lease, manager, blocked).ReconcilePending();
                RequireUnknownControl();
                var state = AttemptWriterState.Read(instance, isolated, new(), fixture.Parent);
                Assert.True(blocked.IsBlocked(state.Journal.ResourceKeys.ToHashSet(StringComparer.Ordinal), out _));
                _ = restart.Recover(lease); RequireUnknownControl();
            }
            return;

            void RequireCaptureRefusal()
            {
                var admitted = StateDatabase.OpenExisting(instance,
                    Path.Combine(source.Root, "capture-pending-diagnostics", Guid.NewGuid().ToString("N")),
                    new JournalStartupAdmission(instance, new()));
                var refusal = Assert.Throws<IOException>(() => new StartupJournalRecovery(instance, new(), admitted,
                    new(Path.Combine(source.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease));
                Assert.Equal("execution_capture_collection_pending", refusal.Message);
            }
            void RequireUnknownControl()
            {
                var held = AttemptWriterState.Read(instance, source.Database, new(), fixture.Parent);
                Assert.Equal(JournalPhase.Quarantined, held.Journal.Phase);
                Assert.Empty(AndroidCollectedCodec.Read(held.Latest.Root, held.Total, held.Attempt.ExecutionPlan.Sha256));
                Assert.Null(new AttemptReceiptStore(source.Database).Read(fixture.Attempt));
                Assert.Equal(installed, File.ReadAllBytes(source.Configuration));
                Assert.Equal(identity, PhysicalObjectIdentity.Read(source.Configuration));
                Assert.NotEqual(fixture.OriginalBytes, installed);
                var saved = ReadCapture(instance, source.Database, held);
                Assert.Equal(original.State, saved.State);
                Assert.Equal(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(original.Source),
                    System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(saved.Source));
                Assert.Equal(original.Root, saved.Root);
                if (captured)
                {
                    using var opened = new CaptureEvidenceReader(instance, source.Database, new())
                        .OpenCaptured(saved.State.Binding, saved.State.Result!);
                    using var bytes = new MemoryStream(); opened.CopyTo(bytes); Assert.Equal(png, bytes.ToArray());
                }
                else Assert.Null(saved.Result);
                RequireNoReplay();
            }
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
        RequireNoReplay();
    }

    private static CaptureSavedEvidence ReadCapture(RuntimeInstance instance, StateDatabase database,
        AttemptWriterState state) => Assert.Single(new CaptureEvidenceReader(instance, database, new())
            .ReadMetadata(state.Journal, state.Attempt, state.Total,
                new JournalStore(instance, new()).ReadAllRevisions(state.Journal.JournalId)));

    private static string ContentPath(RuntimeInstance instance, BlobReference reference) =>
        Path.Combine(instance.RuntimeRoot, reference.RelativePath.Replace('/', Path.DirectorySeparatorChar));
}
