using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallationRecoveryTests
{
    [Fact(DisplayName = "A Replacement At The Removed Target Is Preserved And Cannot Produce A Removal Commit")]
    public async Task PreservesReplacedTarget()
    {
        await using var fixture = new InstallationCrashFixture("applied", PayloadAction.Remove);
        await fixture.StartAsync(); _ = await fixture.StopAsync();
        var target = Path.Combine(fixture.Roots.ExtensionPayloadRoot, "fixture-plugin");
        Directory.CreateDirectory(target);
        var file = Path.Combine(target, "unknown.bin"); var bytes = new byte[] { 3, 8, 2 };
        File.WriteAllBytes(file, bytes); var identity = PhysicalObjectIdentity.Read(file);
        var history = fixture.History();
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Package.Root, "inspection"),
            new JournalStartupAdmission(fixture.Instance, new(), fixture.Roots));
        Assert.ThrowsAny<IOException>(() =>
            new StartupJournalRecovery(fixture.Instance, new(), database, fixture.Roots).Recover(lease));
        Assert.Equal(bytes, File.ReadAllBytes(file)); Assert.Equal(identity, PhysicalObjectIdentity.Read(file));
        var after = fixture.History(); Assert.Equal(history.Length, after.Length);
        for (var i = 0; i < history.Length; i++) Assert.Equal(history[i], after[i]);
    }

    [Theory(DisplayName = "A Stopped Removal Owner With A Complete Verified Physical Prefix Recovers Without Repeating Effects")]
    [InlineData("applied")]
    [InlineData("committed")]
    public async Task RecoversRemovedPrefix(string point)
    {
        await using var fixture = new InstallationCrashFixture(point, PayloadAction.Remove);
        await fixture.StartAsync();
        var physical = fixture.History();
        var stopped = await fixture.StopAsync(); Assert.True(stopped.CompleteJobStopped);
        Assert.All(stopped.Family, item => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(item)));
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Package.Root, "inspection"),
            new JournalStartupAdmission(fixture.Instance, new(), fixture.Roots));
        var settings = new HostSettingsStore(database).Read();
        var index = new StartupJournalRecovery(fixture.Instance, new(), database, fixture.Roots).Recover(lease);
        Assert.True(index.Current.IsReady);
        var state = new ExtensionQueryService(fixture.Instance, lease, database, new(), fixture.Roots)
            .Read(fixture.OperationId, "local:installation-owner")!;
        Assert.Equal("completed", state.Operation.Status);
        Assert.Equal(NexusPipeline.Core.Extensions.ExtensionApplyOutcome.Removed, state.Outcome);
        Assert.Equal(2, state.ResultInstalledRevision);
        Assert.False(state.ResultIsInstalled);
        Assert.Equal(settings.Revision, new HostSettingsStore(database).Read().Revision);
        Assert.False(Directory.Exists(Path.Combine(fixture.Roots.ExtensionPayloadRoot, "fixture-plugin")));
        var after = fixture.History();
        for (var i = 0; i < physical.Length; i++) Assert.Equal(physical[i], after[i]);
        _ = new StartupJournalRecovery(fixture.Instance, new(), database, fixture.Roots).Recover(lease);
        Assert.Equal(after.Length, fixture.History().Length);
    }

    [Theory(DisplayName = "A Real Stopped Installation Owner Reconciles Only Its Recorded Commit And Never Replays An Unknown Callback")]
    [InlineData("committed")]
    [InlineData("applied")]
    [InlineData("callback")]
    public async Task ReconcilesRecordedCommit(string point)
    {
        await using var fixture = new InstallationCrashFixture(point); await fixture.StartAsync();
        var physical = fixture.History(); var target = Path.Combine(fixture.Roots.ExtensionPayloadRoot, "fixture-plugin");
        var targetId = PhysicalObjectIdentity.Read(target); var manifest = File.ReadAllBytes(Path.Combine(target, "manifest.json"));
        var stopped = await fixture.StopAsync(); Assert.True(stopped.CompleteJobStopped);
        Assert.Contains(stopped.Family, item => item.ProcessId == fixture.Native.ProcessId && item.CreatedAtUtc == fixture.Native.CreatedAtUtc);
        Assert.All(stopped.Family, item => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(item)));
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Package.Root, "inspection"),
            new JournalStartupAdmission(fixture.Instance, new(), fixture.Roots));
        var recovery = new StartupJournalRecovery(fixture.Instance, new(), database, fixture.Roots);
        if (point == "committed")
        {
            var index = recovery.Recover(lease); Assert.True(index.Current.IsReady);
            var bound = new ExtensionPayloadBinding(fixture.Instance, database, new(), fixture.Roots).Read(fixture.JournalId, fixture.Plan);
            Assert.Equal(JournalPhase.Committed, bound.Registered.Phase); Assert.NotNull(bound.Footprint);
            Assert.Equal(1, bound.Commit!.InstalledRevision);
            Assert.True(Assert.Single(new HostSettingsStore(database).Read().Values.Extensions).Enabled);
            Assert.Equal(physical.Length, bound.History.Length);
            _ = new StartupJournalRecovery(fixture.Instance, new(), database, fixture.Roots).Recover(lease);
        }
        else
        {
            var error = Assert.Throws<IOException>(() => recovery.Recover(lease));
            Assert.Equal("extension_payload_entered_recovery_pending", error.Message);
            using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
            using var transaction = connection.BeginTransaction(deferred: true);
            Assert.Null(ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, "fixture-plugin"));
            Assert.NotEmpty(new RecoveryJournalRegistry(database).ReadSnapshot().Isolations);
            if (point == "callback") Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "activated.json")));
        }
        var after = fixture.History(); Assert.Equal(physical.Length, after.Length);
        for (var i = 0; i < physical.Length; i++) Assert.Equal(physical[i], after[i]);
        Assert.Equal(targetId, PhysicalObjectIdentity.Read(target)); Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(target, "manifest.json")));
    }

    [Fact(DisplayName = "A Recorded Commit Recovery Sql Failure Rolls Back And The Same Real Recovery Ownership Can Reconcile It Exactly")]
    public async Task RevertsFailedRecovery()
    {
        await using var fixture = new InstallationCrashFixture("committed"); await fixture.StartAsync();
        _ = await fixture.StopAsync();
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Package.Root, "inspection"),
            new JournalStartupAdmission(fixture.Instance, new(), fixture.Roots));
        var registry = new RecoveryJournalRegistry(database); var before = registry.ReadSnapshot();
        new StartupJournalRecovery(fixture.Instance, new(), database, fixture.Roots).InspectBindings(before);
        var ownership = new JournalRecoveryOwnership(database);
        using (ownership.Begin(before.Journals))
        {
            Sql(database, "CREATE TRIGGER fixture_install_recovery_failure BEFORE UPDATE OF phase ON operation_journals " +
                "WHEN NEW.operation_kind='extensionApply' AND NEW.phase='committed' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
            var settings = new HostSettingsStore(database).Read();
            var reconciler = new ExtensionCommittedReconciliation(fixture.Instance, database, new(), fixture.Roots, lease, ownership);
            try { Assert.Throws<SqliteException>(() => reconciler.Reconcile(fixture.JournalId)); }
            finally { Sql(database, "DROP TRIGGER fixture_install_recovery_failure;"); }
            Assert.Equal(settings.Revision, new HostSettingsStore(database).Read().Revision);
            Assert.Equal(JournalPhase.Applied, registry.ReadJournal(fixture.JournalId).Phase);
            using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
            using (var transaction = connection.BeginTransaction(deferred: true))
                Assert.Null(ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, "fixture-plugin"));
            Assert.Equal(JournalRecoveryResult.Completed, reconciler.Reconcile(fixture.JournalId));
        }
        var index = new StartupJournalRecovery(fixture.Instance, new(), database, fixture.Roots).Recover(lease);
        Assert.True(index.Current.IsReady);
        var bound = new ExtensionPayloadBinding(fixture.Instance, database, new(), fixture.Roots).Read(fixture.JournalId, fixture.Plan);
        Assert.Equal(JournalPhase.Committed, bound.Registered.Phase);
        Assert.Equal(1, bound.Commit!.InstalledRevision);
        Assert.Single(new HostSettingsStore(database).Read().Values.Extensions);
    }

    private static void Sql(StateDatabase database, string statement)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath);
        using var command = connection.CreateCommand(); command.CommandText = statement; command.ExecuteNonQuery();
    }
}
