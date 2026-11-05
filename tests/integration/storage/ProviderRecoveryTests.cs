using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ProviderRecoveryTests
{
    [Fact(DisplayName = "Killing An Actual Provider Owner Without A Full Closure Rejects Writable Startup And Preserves The Installed Scene")]
    public async Task PreservesUnclosedScene()
    {
        await using var fixture = await ProviderCrashFixture.StartAsync(collected: false);
        await fixture.KillOwnerAsync();
        var installed = File.ReadAllBytes(fixture.Source.Configuration);
        var marker = File.ReadAllBytes(fixture.Fixture.Marker);
        var instance = new InstanceRoot(new()).OpenExisting(fixture.Source.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(instance, new());
        var before = fixture.Source.Snapshot().Rows;
        var original = AttemptWriterState.Read(instance, fixture.Source.Database, new(), fixture.Parent);
        var journal = original.Latest.Root.GetRawText();
        for (var repetition = 0; repetition < 2; repetition++)
        {
            var error = Assert.Throws<IOException>(() => StateDatabase.OpenExisting(instance,
                Path.Combine(fixture.Source.Root, "restart-diagnostics"), new JournalStartupAdmission(instance, new())));
            Assert.Equal("startup_journal_writer_stop_unconfirmed", error.Message);
            var state = AttemptWriterState.Read(instance, fixture.Source.Database, new(), fixture.Parent);
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(original.Journal), JsonSerializer.SerializeToElement(state.Journal)));
            Assert.Equal(journal, state.Latest.Root.GetRawText());
            Assert.Null(new AttemptReceiptStore(fixture.Source.Database).Read(fixture.Attempt));
            Assert.Empty(ProviderTerminalCodec.Read(state.Latest.Root, state.Total));
            Assert.Equal(installed, File.ReadAllBytes(fixture.Source.Configuration));
            Assert.Equal(marker, File.ReadAllBytes(fixture.Fixture.Marker));
            Assert.Equal(before, fixture.Source.Snapshot().Rows);
        }
    }

    [Fact(DisplayName = "Killing An Actual Provider Owner After Durable Collection Restores Originals And Protects Its Saved Control Transcript From Gc")]
    public async Task ProtectsCollectedTranscript()
    {
        await using var fixture = await ProviderCrashFixture.StartAsync(collected: true);
        await fixture.KillOwnerAsync();
        var marker = File.ReadAllBytes(fixture.Fixture.Marker);
        var instance = new InstanceRoot(new()).OpenExisting(fixture.Source.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenExisting(instance, Path.Combine(fixture.Source.Root, "restart-diagnostics"), new JournalStartupAdmission(instance, new()));
        var recovery = new StartupJournalRecovery(instance, new(), database,
            new(Path.Combine(fixture.Source.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions")));
        var index = recovery.Recover(lease);
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        new RecoveryCompletionService(instance, database, new(), lease, resources, index).ReconcilePending();
        var state = AttemptWriterState.Read(instance, database, new(), fixture.Parent);
        Assert.Equal(JournalPhase.Committed, state.Journal.Phase);
        var receipt = Assert.IsType<AttemptReceipt>(new AttemptReceiptStore(database).Read(fixture.Attempt));
        Assert.Equal(LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle);
        Assert.Equal(EngineOutcome.Succeeded, receipt.Facts.Engine);
        Assert.Equal(BusinessOutcome.NotApplicable, receipt.Facts.Business);
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(fixture.OriginalBytes, File.ReadAllBytes(fixture.Source.Configuration));
        Assert.Equal(fixture.OriginalIdentity, PhysicalObjectIdentity.Read(fixture.Source.Configuration));
        Assert.False(index.IsBlocked(state.Journal.ResourceKeys.ToHashSet(StringComparer.Ordinal), out _));
        var terminal = Assert.Single(ProviderTerminalCodec.Read(state.Latest.Root, state.Total));
        var control = new ProviderControlReader(instance, database, new()).Read(terminal.Binding, terminal.Artifact);
        Assert.Equal(terminal.Decision, control.Terminal);
        var raw = Assert.Single(control.Archive.Parts);
        var blobs = new ImmutableBlobStore(instance, new());
        var references = new BlobReferenceIndex(instance, new(), database, blobs);
        using (var exclusive = new FileStream(Path.Combine(instance.RuntimeRoot, raw.RelativePath), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.True(references.IsReferenced(raw));
        var candidate = Assert.Single(new CleanupPlanCodec(instance, new(), blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), [raw]).Items);
        Assert.Equal(CleanupStepResult.SkippedReferenced, new BlobCleanup(instance, blobs, references).RemoveVerified(candidate));
        blobs.Verify(raw);
        Assert.Equal(marker, File.ReadAllBytes(fixture.Fixture.Marker));
        _ = recovery.Recover(lease);
        Assert.Equal(receipt, new AttemptReceiptStore(database).Read(fixture.Attempt));
        Assert.Equal(marker, File.ReadAllBytes(fixture.Fixture.Marker));
    }
}
