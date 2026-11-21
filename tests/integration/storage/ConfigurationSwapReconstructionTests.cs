using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed partial class ConfigurationSwapTests
{
    [Fact(DisplayName = "Configuration Recovery Uses Its Adjacent Original Even When Immutable Content Is Missing")]
    [SupportedOSPlatform("windows")]
    public void RestoresOriginalWithoutArchive()
    {
        using var fixture = new AppliedConfiguration();
        File.Delete(fixture.BeforeBlob);
        Assert.Equal(ConfigurationRecoveryResult.Restored, fixture.Recover());
        AssertOriginal(fixture.Site);
    }

    [Theory(DisplayName = "Configuration Recovery Rebuilds Only A Recognized Scene From Its Exact Immutable Original")]
    [InlineData("archive", ConfigurationRecoveryResult.Restored, "configuration_recovery_observed")]
    [InlineData("missing", ConfigurationRecoveryResult.Quarantined, "configuration_original_lost")]
    [InlineData("corrupt", ConfigurationRecoveryResult.Quarantined, "configuration_backup_unverified")]
    [InlineData("foreign-target", ConfigurationRecoveryResult.Quarantined, "configuration_state_unverified")]
    [InlineData("foreign-backup", ConfigurationRecoveryResult.Quarantined, "configuration_state_unverified")]
    [SupportedOSPlatform("windows")]
    public void RecoversMissingOriginal(string scene, ConfigurationRecoveryResult expected, string reason)
    {
        using var fixture = new AppliedConfiguration();
        var plan = fixture.Site.Plan;
        var target = plan.TargetPath;
        var heldOriginal = Path.Combine(fixture.Site.Root, "missing-original");
        Directory.Move(fixture.Before, heldOriginal);
        if (scene == "missing") File.Delete(fixture.BeforeBlob);
        if (scene == "corrupt") File.WriteAllBytes(fixture.BeforeBlob, [2, 4, 6]);
        if (scene == "foreign-target") File.WriteAllBytes(target, [6, 4, 2]);
        if (scene == "foreign-backup") File.WriteAllBytes(fixture.Before, [6, 4, 2]);
        var existingTarget = File.ReadAllBytes(target);
        var existingIdentity = PhysicalObjectIdentity.Read(target);

        Assert.Equal(expected, fixture.Recover());
        Assert.Equal(reason, fixture.Journal.Root.GetProperty("receipts").EnumerateArray().Last().GetProperty("detailCode").GetString());
        if (expected == ConfigurationRecoveryResult.Restored)
        {
            var restored = fixture.Site.Scanner.Capture(target, default);
            Assert.Equal(plan.Before.Snapshot.Entries, restored.Entries);
            Assert.NotEqual(plan.Before.OriginalIdentity, PhysicalObjectIdentity.Read(target));
            Assert.True(Directory.Exists(Path.Combine(target, "nested", "empty")));
            Assert.Equal(ConfigurationRecoveryResult.AlreadyCompleted, fixture.Recover());
        }
        else
        {
            Assert.Equal(existingTarget, File.ReadAllBytes(target));
            Assert.Equal(existingIdentity, PhysicalObjectIdentity.Read(target));
            if (scene == "foreign-backup") Assert.Equal(new byte[] { 6, 4, 2 }, File.ReadAllBytes(fixture.Before));
        }
    }

    [Theory(DisplayName = "A Restart Uses The Recorded Reconstruction Identity Even After The Archive Disappears")]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("windows")]
    public void ReplaysReconstructionProof(bool replaceStaged)
    {
        using var fixture = new AppliedConfiguration();
        var plan = fixture.Site.Plan;
        Directory.Move(fixture.Before, Path.Combine(fixture.Site.Root, "missing-original"));
        Assert.Throws<InterruptedAfterReconstruction>(() => fixture.Recover(new InterruptReconstruction(fixture)));
        Assert.Equal(JournalPhase.Reverting, fixture.Journal.Phase);
        Assert.Equal(plan.Candidate.Snapshot.Entries, fixture.Site.Scanner.Capture(plan.TargetPath, default).Entries);
        var staged = Path.Combine(Path.GetDirectoryName(fixture.Before)!, "reconstructed");
        var stagedIdentity = PhysicalObjectIdentity.Read(staged);
        if (replaceStaged)
        {
            Directory.Move(staged, Path.Combine(fixture.Site.Root, "first-reconstruction"));
            fixture.Site.Archive.MaterializeNew(plan.Before, staged, default);
        }
        File.Delete(fixture.BeforeBlob);

        Assert.Equal(replaceStaged ? ConfigurationRecoveryResult.Quarantined : ConfigurationRecoveryResult.Restored, fixture.Recover());
        if (replaceStaged)
        {
            Assert.Equal(plan.Candidate.Snapshot.Entries, fixture.Site.Scanner.Capture(plan.TargetPath, default).Entries);
            Assert.NotEqual(stagedIdentity, PhysicalObjectIdentity.Read(staged));
        }
        else
        {
            Assert.Equal(stagedIdentity, PhysicalObjectIdentity.Read(plan.TargetPath));
            Assert.Equal(plan.Before.Snapshot.Entries, fixture.Site.Scanner.Capture(plan.TargetPath, default).Entries);
            Assert.Equal(ConfigurationRecoveryResult.AlreadyCompleted, fixture.Recover());
        }
    }

    [Theory(DisplayName = "A Reconstruction Intent Resumes A Complete Staged Original And Preserves An Incomplete Stage")]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("windows")]
    public void ReplaysStagedOriginal(bool incomplete)
    {
        using var fixture = new AppliedConfiguration();
        var plan = fixture.Site.Plan;
        Directory.Move(fixture.Before, Path.Combine(fixture.Site.Root, "missing-original"));
        var staged = Path.Combine(Path.GetDirectoryName(fixture.Before)!, "reconstructed");
        Assert.Throws<InterruptedAfterReconstruction>(() => fixture.Recover(new InterruptReconstruction(fixture, () =>
        {
            fixture.Site.Archive.MaterializeNew(plan.Before, staged, default);
            if (incomplete) File.Delete(Path.Combine(staged, "nested", "配置"));
        })));
        var stagedIdentity = PhysicalObjectIdentity.Read(staged);
        File.Delete(fixture.BeforeBlob);

        Assert.Equal(incomplete ? JournalRecoveryResult.Quarantined : JournalRecoveryResult.Restored, fixture.RecoverAtStartup());
        if (incomplete)
        {
            Assert.Equal(stagedIdentity, PhysicalObjectIdentity.Read(staged));
            Assert.Equal(plan.Candidate.Snapshot.Entries, fixture.Site.Scanner.Capture(plan.TargetPath, default).Entries);
            Assert.Equal(JournalRecoveryResult.Quarantined, fixture.RecoverAtStartup());
        }
        else
        {
            Assert.Equal(stagedIdentity, PhysicalObjectIdentity.Read(plan.TargetPath));
            Assert.Equal(plan.Before.Snapshot.Entries, fixture.Site.Scanner.Capture(plan.TargetPath, default).Entries);
            Assert.Equal(JournalRecoveryResult.AlreadyCompleted, fixture.RecoverAtStartup());
        }
    }

    [Fact(DisplayName = "Startup Retries An Isolated Configuration When Its Exact Archive Has Returned")]
    [SupportedOSPlatform("windows")]
    public void RetriesIsolatedConfiguration()
    {
        using var fixture = new AppliedConfiguration();
        var plan = fixture.Site.Plan;
        var original = Path.Combine(fixture.Site.Root, "missing-original");
        Directory.Move(fixture.Before, original);
        File.Move(fixture.BeforeBlob, Path.Combine(fixture.Site.Root, "archive-copy"));
        Assert.Equal(ConfigurationRecoveryResult.Quarantined, fixture.Recover());
        Assert.Equal(JournalRecoveryResult.Quarantined, fixture.RecoverAtStartup());
        File.Move(Path.Combine(fixture.Site.Root, "archive-copy"), fixture.BeforeBlob);

        Assert.Equal(JournalRecoveryResult.Restored, fixture.RecoverAtStartup());
        Assert.Equal(JournalPhase.Committed, fixture.Journal.Phase);
        Assert.Equal(plan.Before.Snapshot.Entries, fixture.Site.Scanner.Capture(plan.TargetPath, default).Entries);
        Assert.Empty(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Isolations);
    }

    [SupportedOSPlatform("windows")]
    private sealed class InterruptReconstruction(AppliedConfiguration fixture, Action? writeStage = null) : IConfigurationPersistenceObserver
    {
        public void Reached(ConfigurationPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            if (point != ConfigurationPersistencePoint.JournalFlushed || phase != JournalPhase.Reverting) return;
            var physical = fixture.Journal;
            if (writeStage is null)
            {
                if (physical.Root.GetProperty("participants").GetArrayLength() == 2) throw new InterruptedAfterReconstruction();
                return;
            }
            var receipt = physical.Root.GetProperty("receipts").EnumerateArray().Last();
            if (receipt.GetProperty("stepId").GetString() != "prepare-reconstruction" || receipt.GetProperty("effect").GetString() != "started") return;
            writeStage();
            throw new InterruptedAfterReconstruction();
        }
    }

    private sealed class InterruptedAfterReconstruction : Exception;

    [SupportedOSPlatform("windows")]
    private sealed class AppliedConfiguration : IDisposable
    {
        private readonly ContractReader _contracts = new();
        private readonly RuntimeInstance _instance;
        private readonly InstanceLease _lease;
        internal StateDatabase Database { get; }
        internal Fixture Site { get; }
        internal string Before { get; }
        internal string BeforeBlob => Path.Combine(Site.RuntimeRoot, Assert.Single(Site.Plan.Before.Contents).RelativePath);
        internal JournalDocument Journal => new JournalStore(_instance, _contracts).Read(Site.Plan.JournalId);

        internal AppliedConfiguration()
        {
            StateDatabase? database = null;
            Site = Create("directory", "file", instance => database = StateDatabase.CreateFresh(instance));
            _instance = new InstanceRoot(_contracts).OpenExisting(Site.RuntimeRoot);
            _lease = InstanceLease.Acquire(_instance, _contracts);
            Database = database!;
            var reference = Site.PersistPlan();
            var plan = Site.Plan;
            var operation = new OperationIdentity(plan.OperationId, "fixture", new string('a', 64), "configuration.exchange");
            var registered = new RegisteredJournal(plan.JournalId, plan.OperationId, plan.Kind, JournalPhase.Prepared,
                1, reference.RelativePath, reference.Sha256, ["configuration:restoration-fixture"]);
            new RecoveryJournalRegistry(Database).AcceptPrepared(operation, registered,
                ConfigurationFileEffects.Create(plan.JournalId, reference.Sha256), DateTimeOffset.UtcNow);
            new ConfigurationEditExchange(_instance, Database, _contracts).Install(plan.JournalId);
            Before = Path.Combine(Path.GetDirectoryName(plan.TargetPath)!, ".nexus-" + plan.JournalId.ToString("D"), "before");
        }

        internal ConfigurationRecoveryResult Recover(IConfigurationPersistenceObserver? observer = null) =>
            new ConfigurationEditExchange(_instance, Database, _contracts).Restore(Site.Plan.JournalId, observer);

        internal JournalRecoveryResult RecoverAtStartup()
        {
            var journal = new RecoveryJournalRegistry(Database).ReadJournal(Site.Plan.JournalId);
            using var work = new ConfigurationRecoveryHandler(_instance, _contracts, Database, journal.Kind, _ => Site.Stopped, _lease).Prepare(journal);
            return work.Execute();
        }
        public void Dispose() => _lease.Dispose();
    }
}
