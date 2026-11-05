using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CompletionPreflightTests
{
    [Theory(DisplayName = "A Structurally Valid But Unbound Intent Is Rejected Before Opening Or Recovering Can Change Its Persistent Bytes")]
    [InlineData("intent-synced", "subjectId")]
    [InlineData("intent-synced", "evidenceDigest")]
    [InlineData("intent-ahead", "subjectId")]
    [InlineData("intent-ahead", "evidenceDigest")]
    public async Task PreservesUnboundIntents(string point, string field)
    {
        using var fixture = new CompletionPreflightFixture(point); await fixture.StopOwnerAsync();
        fixture.DamageIntent(field); var before = fixture.PersistentBytes();
        var instance = new InstanceRoot(new()).OpenExisting(fixture.Run.Source.Instance.RuntimeRoot);
        using var lease = InstanceLease.Acquire(instance, new());
        var error = Assert.Throws<IOException>(() => StateDatabase.OpenExisting(instance,
            Path.Combine(fixture.Run.Source.Root, "intent-rejected-diagnostics"), new JournalStartupAdmission(instance, new())));
        Assert.Contains("configuration_snapshot_completion", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, fixture.PersistentBytes());
        var recovery = new StartupJournalRecovery(instance, new(), fixture.Run.Source.Database,
            new(fixture.Run.Source.Root, Path.Combine(instance.RuntimeRoot, "extensions")));
        Assert.Throws<IOException>(() => recovery.Recover(lease));
        Assert.Equal(before, fixture.PersistentBytes()); fixture.Run.VerifyOriginal();
    }

    [Theory(DisplayName = "A Real Stopped Owners Pending Or Fulfilled Intent Passes Read Only Admission And Recovers Only Its Original Completion")]
    [InlineData("intent-synced")]
    [InlineData("intent-ahead")]
    [InlineData("intent-fulfilled")]
    public async Task RecoversOriginalCompletion(string point)
    {
        using var fixture = new CompletionPreflightFixture(point); await fixture.StopOwnerAsync();
        var target = Path.Combine(fixture.Run.Request.Resources[0].Target.TargetPath, "settings.bin");
        var original = File.ReadAllBytes(target); var identity = PhysicalObjectIdentity.Read(target);
        if (point == "intent-fulfilled") File.WriteAllBytes(target, [77, 88]);
        try
        {
            var before = fixture.PersistentBytes();
            var instance = new InstanceRoot(new()).OpenExisting(fixture.Run.Source.Instance.RuntimeRoot);
            using var lease = InstanceLease.Acquire(instance, new());
            var database = StateDatabase.OpenExisting(instance, Path.Combine(fixture.Run.Source.Root, "intent-valid-diagnostics"),
                new JournalStartupAdmission(instance, new()));
            Assert.Equal(before, fixture.PersistentBytes());
            var index = new StartupJournalRecovery(instance, new(), database,
                new(fixture.Run.Source.Root, Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
            var session = new ConfigurationRunStore(instance, database, new()).Read(fixture.SessionId, ConfigurationRunFixture.Subject)!;
            var parent = Assert.Single(new RecoveryJournalRegistry(database).ReadSnapshot().Journals, item => item.JournalId == fixture.ParentId);
            Assert.Equal(JournalPhase.Prepared, parent.Phase); Assert.Equal(2, parent.Revision);
            Assert.Equal(0L, session.ClosedElapsedMilliseconds); Assert.False(session.EnteredRunning);
            Assert.Equal(point == "intent-fulfilled" ? 0 : 1, index.Current.SnapshotCompletions.Length);
            if (point != "intent-fulfilled") Assert.Equal(fixture.CompletionId, Assert.Single(index.Current.SnapshotCompletions).CompletionOperationId);
            var manager = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
            var completion = new RunCompletionService(instance, database, new(), lease, manager, index);
            var result = completion.CompleteInterrupted(fixture.CompletionId, ConfigurationRunFixture.Subject, fixture.SessionId);
            Assert.Equal(point != "intent-fulfilled", result.IsNew); Assert.Equal(ConfigurationRunPhase.Interrupted, result.Session.Phase);
            Assert.Equal(fixture.CompletionId, result.Session.CompletionOperationId);
            Assert.All(result.Items, item => { Assert.Equal(1, item.PreviousRevision); Assert.Equal(1, item.CurrentRevision); Assert.Equal("configuration_sync_not_due", item.ReasonCode); });
            Assert.Empty(index.Current.ResourceHolds); Assert.Empty(index.Current.SnapshotCompletions);
            Assert.Contains(index.Current.Isolations, item => item.JournalId == fixture.ParentId);
            Assert.False(completion.CompleteInterrupted(fixture.CompletionId, ConfigurationRunFixture.Subject, fixture.SessionId).IsNew);
            ConfigurationRunInventory.VerifyReadOnly(instance, database, new());
            Assert.Equal(identity, PhysicalObjectIdentity.Read(target));
            if (point == "intent-fulfilled") Assert.Equal(new byte[] { 77, 88 }, File.ReadAllBytes(target));
        }
        finally { if (point == "intent-fulfilled") File.WriteAllBytes(target, original); }
        fixture.Run.VerifyOriginal();
    }
}
