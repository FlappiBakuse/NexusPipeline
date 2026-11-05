using System.Runtime.Versioning;
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
public sealed class AndroidRecoveryTests
{
    [Fact(DisplayName = "Killing The Actual Owner After Command Root Admission Cannot Invent Completion Or Replay Its Unknown Remote Effect")]
    public async Task KeepsUnknownEffects()
    {
        using var fixture = new AndroidCrashFixture();
        await fixture.StartAsync("RootAdmitted");
        Assert.Empty(fixture.ObservedCommands());
        Assert.False(File.Exists(fixture.Fixture.Marker + ".main"));
        await fixture.KillOwnerAsync();
        var before = File.ReadAllBytes(fixture.Source.Configuration);
        Assert.NotEqual(fixture.OriginalBytes, before);
        var source = fixture.Source;
        var instance = new InstanceRoot(new()).OpenExisting(source.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenExisting(instance, Path.Combine(source.Root, "restart-diagnostics"), new JournalStartupAdmission(instance, new()));
        var recovery = new StartupJournalRecovery(instance, new(), database,
            new(Path.Combine(source.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions")));
        var index = recovery.Recover(lease);
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        new RecoveryCompletionService(instance, database, new(), lease, resources, index).ReconcilePending();
        var state = AttemptWriterState.Read(instance, database, new(), fixture.Parent);
        Assert.Equal(JournalPhase.Quarantined, state.Journal.Phase);
        Assert.Null(new AttemptReceiptStore(database).Read(fixture.Attempt));
        Assert.True(index.IsBlocked(state.Journal.ResourceKeys.ToHashSet(StringComparer.Ordinal), out _));
        Assert.Equal(before, File.ReadAllBytes(source.Configuration));
        Assert.Empty(fixture.ObservedCommands());
        Assert.False(File.Exists(fixture.Fixture.Marker + ".main"));
        Assert.Empty(AndroidCollectedCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
        var repeated = recovery.Recover(lease);
        Assert.True(repeated.IsBlocked(state.Journal.ResourceKeys.ToHashSet(StringComparer.Ordinal), out _));
        Assert.Equal(before, File.ReadAllBytes(source.Configuration));
        Assert.Empty(fixture.ObservedCommands());
    }

    [Fact(DisplayName = "Killing The Actual Owner After Durable Command And Main Collection Restores Originals And Keeps Its Interrupted Facts")]
    public async Task RestoresCollectedAttempts()
    {
        using var fixture = new AndroidCrashFixture();
        await fixture.StartAsync("AndroidCollectedPublished");
        var observed = fixture.ObservedCommands();
        Assert.NotEmpty(observed);
        Assert.True(File.Exists(fixture.Fixture.Marker + ".main"));
        Assert.NotEqual(fixture.OriginalBytes, File.ReadAllBytes(fixture.Source.Configuration));
        await fixture.KillOwnerAsync();
        var source = fixture.Source;
        var instance = new InstanceRoot(new()).OpenExisting(source.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(instance, new());
        var database = StateDatabase.OpenExisting(instance, Path.Combine(source.Root, "restart-diagnostics"), new JournalStartupAdmission(instance, new()));
        var recovery = new StartupJournalRecovery(instance, new(), database,
            new(Path.Combine(source.Root, "host"), Path.Combine(instance.RuntimeRoot, "extensions")));
        var index = recovery.Recover(lease);
        var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
        new RecoveryCompletionService(instance, database, new(), lease, resources, index).ReconcilePending();
        var state = AttemptWriterState.Read(instance, database, new(), fixture.Parent);
        Assert.Equal(JournalPhase.Committed, state.Journal.Phase);
        var receipt = Assert.IsType<AttemptReceipt>(new AttemptReceiptStore(database).Read(fixture.Attempt));
        Assert.Equal(LifecycleOutcome.Interrupted, receipt.Facts.Lifecycle);
        Assert.Equal(BusinessOutcome.Succeeded, receipt.Facts.Business);
        Assert.Equal(RecoveryOutcome.Restored, receipt.Facts.Recovery);
        Assert.Equal(fixture.OriginalBytes, File.ReadAllBytes(source.Configuration));
        Assert.Equal(fixture.OriginalIdentity, PhysicalObjectIdentity.Read(source.Configuration));
        Assert.False(index.IsBlocked(state.Journal.ResourceKeys.ToHashSet(StringComparer.Ordinal), out _));
        var collected = Assert.Single(AndroidCollectedCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256));
        Assert.NotEmpty(new AndroidResultEvidence(instance, database, new()).RequireCollected(state, collected, true));
        var blobs = new ImmutableBlobStore(instance, new());
        var reader = new AndroidControlEvidence(instance, database, new());
        var document = reader.Read<AndroidCollectedDocument>(collected.Artifact);
        var command = AndroidControlCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)[0];
        var commandClosure = reader.Collection(state, command, document.Commands[0].Collection, false);
        var raw = reader.Observation(state, command, commandClosure.Observation, false).Parts[0].Content;
        var references = new BlobReferenceIndex(instance, new(), database, blobs);
        using (var exclusive = new FileStream(Path.Combine(instance.RuntimeRoot, raw.RelativePath), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.True(references.IsReferenced(raw));
        var candidate = Assert.Single(new CleanupPlanCodec(instance, new(), blobs).Capture(Guid.NewGuid(), Guid.NewGuid(), [raw]).Items);
        Assert.Equal(CleanupStepResult.SkippedReferenced, new BlobCleanup(instance, blobs, references).RemoveVerified(candidate));
        blobs.Verify(raw);
        Assert.Equal(observed, fixture.ObservedCommands());
        _ = recovery.Recover(lease);
        Assert.Equal(receipt, new AttemptReceiptStore(database).Read(fixture.Attempt));
        Assert.Equal(observed, fixture.ObservedCommands());
    }
}
