using System.Text.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
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
public sealed class RevertedRecoveryTests
{
    [Theory(DisplayName = "Actual Stopped Owners Restore Only Their Original Frozen Tree And Commit Reverted Without An Installed Revision")]
    [InlineData(null, "candidate-materialized")]
    [InlineData(null, "candidate-moved")]
    [InlineData(PayloadAction.Upgrade, "candidate-moved")]
    [InlineData(PayloadAction.Remove, "original-preserved")]
    public async Task RestoresFrozenTrees(
        PayloadAction? followup, string stage)
    {
        await using var fixture = new InstallationCrashFixture("physical", followup, stage);
        await fixture.StartAsync();
        var before = fixture.History();
        var stopped = await fixture.StopAsync(); RequireStopped(fixture, stopped);
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var database = Open(fixture);
        var settings = new HostSettingsStore(database).Read();
        var initial = Binding(fixture, database);
        Assert.Null(initial.Commit); Assert.Equal(JournalPhase.Applying, initial.Registered.Phase);
        var original = initial.Plan.Before; var target = initial.Plan.TargetPath;
        var index = new StartupJournalRecovery(fixture.Instance, new(), database, fixture.Roots).Recover(lease);
        Assert.True(index.Current.IsReady);
        var bound = Binding(fixture, database);
        Assert.Equal(JournalPhase.Committed, bound.Registered.Phase);
        Assert.Null(bound.Commit); Assert.Null(bound.Footprint); Assert.Null(bound.Preparation);
        Assert.Equal("committed", bound.Restoration[^1].Record.Stage);
        Assert.Equal("reverted", bound.Latest.Root.GetProperty("receipts")[0].GetProperty("effect").GetString());
        var scanner = new FileTreeScanner(new(4096, 268435456));
        var actual = scanner.Capture(target, default);
        Assert.Equal(original.Snapshot.Shape, actual.Shape); Assert.Equal(original.Snapshot.Entries, actual.Entries);
        Assert.Equal(original.OriginalObjects, PhysicalTreeIdentity.Capture(target, actual));
        Assert.Equal(settings.Revision, new HostSettingsStore(database).Read().Revision);
        AssertInstalledRevision(database, followup.HasValue ? 1 : null);
        var effects = new FileEffectOutbox(database).Read(fixture.JournalId);
        Assert.All(effects, effect => Assert.True(effect.State is FileEffectState.Completed or FileEffectState.Reverted));
        if (followup != PayloadAction.Remove) Assert.Contains(effects, effect => effect.StepId == "install-candidate" && effect.State == FileEffectState.Reverted);
        if (followup.HasValue)
        {
            Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "cleaned.json")));
            Assert.Contains(effects, effect => effect.StepId == "preserve-original" &&
                effect.State == FileEffectState.Completed);
        }
        for (var i = 0; i < before.Length; i++) Assert.Equal(before[i], bound.History[i].Export());
        var committed = bound.Latest.Export();
        _ = new StartupJournalRecovery(fixture.Instance, new(), database, fixture.Roots).Recover(lease);
        Assert.Equal(committed, Binding(fixture, database).Latest.Export());
    }

    [Fact(DisplayName = "Restoration Sql Rollback Preserves The Physical Restored Evidence And Exact Replay Uses The Same Actual Recovery Owner")]
    public async Task ReplaysRestoredEvidence()
    {
        await using var fixture = new InstallationCrashFixture("physical", PayloadAction.Upgrade, "candidate-moved");
        await fixture.StartAsync(); RequireStopped(fixture, await fixture.StopAsync());
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var database = Open(fixture); var registry = new RecoveryJournalRegistry(database);
        var snapshot = registry.ReadSnapshot();
        new StartupJournalRecovery(fixture.Instance, new(), database, fixture.Roots).InspectBindings(snapshot);
        var ownership = new JournalRecoveryOwnership(database);
        using (ownership.Begin(snapshot.Journals))
        {
            var restore = new ExtensionPayloadRestoration(fixture.Instance, database, new(), fixture.Roots, lease, ownership);
            Sql(database, "CREATE TRIGGER main.fixture_reverted_failure BEFORE UPDATE OF phase ON operation_journals " +
                "WHEN NEW.operation_kind='extensionApply' AND NEW.phase='reverted' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
            try { Assert.Throws<SqliteException>(() => restore.Recover(fixture.JournalId)); }
            finally { Sql(database, "DROP TRIGGER main.fixture_reverted_failure;"); }
            var pending = Binding(fixture, database);
            Assert.Equal(JournalPhase.Reverting, pending.Registered.Phase); Assert.Equal(JournalPhase.Reverted, pending.Latest.Phase);
            Assert.Equal("restored", pending.Restoration[^1].Record.Stage);
            var physical = pending.Latest.Export(); var tree = Fingerprint(pending.Plan.TargetPath);
            AssertInstalledRevision(database, 1);
            Assert.Equal(JournalRecoveryResult.Completed, restore.Recover(fixture.JournalId));
            var after = Binding(fixture, database);
            Assert.Equal(physical, after.History.Single(item => item.Revision == pending.Latest.Revision).Export());
            Assert.Equal(tree, Fingerprint(after.Plan.TargetPath)); AssertInstalledRevision(database, 1);
        }
        Assert.True(new StartupJournalRecovery(fixture.Instance, new(), database, fixture.Roots).Recover(lease).Current.IsReady);
    }

    [Fact(DisplayName = "A New Unowned Workspace Entry Rejects Restoration Without Deleting Or Adopting Any Bytes")]
    public async Task PreservesUnownedEntries()
    {
        await using var fixture = new InstallationCrashFixture("physical", PayloadAction.Remove, "original-preserved");
        await fixture.StartAsync(); RequireStopped(fixture, await fixture.StopAsync());
        using var lease = InstanceLease.Acquire(fixture.Instance, new()); var database = Open(fixture);
        var bound = Binding(fixture, database);
        var path = Path.Combine(ExtensionWorkspacePath.For(bound.Plan), "unowned.bin");
        var bytes = new byte[] { 1, 4, 7 }; File.WriteAllBytes(path, bytes); var identity = PhysicalObjectIdentity.Read(path);
        var history = fixture.History();
        Assert.Throws<IOException>(() => new StartupJournalRecovery(fixture.Instance, new(), database, fixture.Roots).Recover(lease));
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Equal(identity, PhysicalObjectIdentity.Read(path));
        Assert.False(Directory.Exists(bound.Plan.TargetPath)); AssertInstalledRevision(database, 1);
        var after = fixture.History(); Assert.Equal(history.Length, after.Length);
        for (var i = 0; i < history.Length; i++) Assert.Equal(history[i], after[i]);
        Assert.NotEmpty(new RecoveryJournalRegistry(database).ReadSnapshot().Isolations);
    }

    [Fact(DisplayName = "Changed Restoration Artifact Cannot Authorize Another Physical Move Or Close The Forward Outbox")]
    public async Task RejectsChangedRestoration()
    {
        await using var fixture = new InstallationCrashFixture("physical", PayloadAction.Upgrade, "candidate-moved");
        await fixture.StartAsync(); RequireStopped(fixture, await fixture.StopAsync());
        using var lease = InstanceLease.Acquire(fixture.Instance, new());
        var database = Open(fixture); var registry = new RecoveryJournalRegistry(database); var before = registry.ReadSnapshot();
        var ownership = new JournalRecoveryOwnership(database);
        using (ownership.Begin(before.Journals))
        {
            var restore = new ExtensionPayloadRestoration(fixture.Instance, database, new(), fixture.Roots, lease, ownership);
            Sql(database, "CREATE TRIGGER main.fixture_reverting_failure BEFORE UPDATE OF phase ON operation_journals " +
                "WHEN NEW.operation_kind='extensionApply' AND NEW.phase='reverting' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
            try { Assert.Throws<SqliteException>(() => restore.Recover(fixture.JournalId)); }
            finally { Sql(database, "DROP TRIGGER main.fixture_reverting_failure;"); }
            var bound = Binding(fixture, database); var recorded = Assert.Single(bound.Restoration);
            var target = Fingerprint(bound.Plan.TargetPath); var cursor = registry.ReadJournal(fixture.JournalId);
            var effects = new FileEffectOutbox(database).Read(fixture.JournalId).ToArray();
            var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(fixture.Instance.RuntimeRoot, recorded.Intent.Artifact.RelativePath));
            var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
            Assert.ThrowsAny<IOException>(() => restore.Recover(fixture.JournalId));
            Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Equal(target, Fingerprint(bound.Plan.TargetPath));
            Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(cursor), JsonSerializer.SerializeToUtf8Bytes(registry.ReadJournal(fixture.JournalId)));
            Assert.Equal(effects, new FileEffectOutbox(database).Read(fixture.JournalId)); AssertInstalledRevision(database, 1);
        }
    }

    private static StateDatabase Open(InstallationCrashFixture fixture) => StateDatabase.OpenExisting(
        fixture.Instance, Path.Combine(fixture.Package.Root, "inspection"), new JournalStartupAdmission(fixture.Instance, new(), fixture.Roots));
    private static BoundExtensionPayload Binding(InstallationCrashFixture fixture, StateDatabase database) =>
        new ExtensionPayloadBinding(fixture.Instance, database, new(), fixture.Roots).Read(fixture.JournalId, fixture.Plan);
    private static void RequireStopped(InstallationCrashFixture fixture, OwnedCrashStop stopped)
    {
        Assert.True(stopped.CompleteJobStopped);
        Assert.Contains(stopped.Family, item => item.ProcessId == fixture.Native.ProcessId && item.CreatedAtUtc == fixture.Native.CreatedAtUtc);
        Assert.All(stopped.Family, item => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(item)));
    }
    private static void AssertInstalledRevision(StateDatabase database, long? expected)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Extensions);
        using var transaction = connection.BeginTransaction(deferred: true);
        var current = ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, "fixture-plugin");
        Assert.Equal(expected, current?.Revision);
    }
    private static string Fingerprint(string path) => string.Join("\n", Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .OrderBy(item => item, StringComparer.Ordinal).Select(item => Path.GetRelativePath(path, item) + ":" +
            PhysicalObjectIdentity.Read(item) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(item)))));
    private static void Sql(StateDatabase database, string statement)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, domains: DatabaseDomain.Runtime);
        using var command = connection.CreateCommand(); command.CommandText = statement; command.ExecuteNonQuery();
    }
}
