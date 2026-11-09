using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Settings;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Testing;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class SettingsStartupTests
{
    [Fact(DisplayName = "Startup Settings Commit Precedes Snapshot And System Effects And Old Replay Cannot Reenable The Task")]
    public void OrdersStartupEffects()
    {
        using var fixture = new Fixture();
        var change = new HostSettingsChange(0, HostSettings.Default with { AutoStart = true });
        var operation = Identity(change);
        fixture.Effects.BeforeCreate = () =>
        {
            var saved = new HostSettingsSnapshot(1, change.Values);
            Assert.Equal(saved, new HostSettingsStore(fixture.Database).Read());
            Assert.Equal(saved, fixture.Service.Read().Saved);
            Assert.False(fixture.Service.Read().Effective.AutoStart);
            Assert.Equal(StartupHolds(operation.OperationId), fixture.Index.Current.ResourceHolds);
            using var unrelated = fixture.Resources.TryAcquire(Guid.NewGuid(), [new("unrelated", ResourceClaimMode.Mutation)]).Lease;
            Assert.NotNull(unrelated);
        };
        var enabled = fixture.Service.Save(operation, change, DateTimeOffset.UtcNow);
        Assert.Equal(new StartupEffectStatus(operation.OperationId, 1, true, StartupEffectState.Completed), enabled.StartupEffect);
        Assert.True(fixture.Service.Read().Effective.AutoStart);
        var definition = System.Xml.Linq.XDocument.Load(fixture.Effects.Path);
        var principal = definition.Descendants().First(item => item.Name.LocalName == "UserId").Value;
        var executable = definition.Descendants().First(item => item.Name.LocalName == "Command").Value;
        Assert.Equal(ProcessIdentityReader.Read(Environment.ProcessId).UserSid, principal);
        Assert.Equal(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE"), executable);
        Assert.Empty(fixture.Index.Current.ResourceHolds);
        var disable = new HostSettingsChange(1, change.Values with { AutoStart = false });
        var disableOperation = Identity(disable);
        var disabled = fixture.Service.Save(disableOperation, disable, DateTimeOffset.UtcNow);
        Assert.Equal(new StartupEffectStatus(disableOperation.OperationId, 2, false, StartupEffectState.Completed), disabled.StartupEffect);
        Assert.False(fixture.Service.Read().Effective.AutoStart);
        Assert.False(File.Exists(fixture.Effects.Path));
        fixture.Effects.BeforeMutation = RejectMutation;
        Assert.Equal(enabled with { IsNew = false }, fixture.Service.Save(operation, change, DateTimeOffset.UtcNow));
        Assert.Equal(disabled.Saved, new HostSettingsStore(fixture.Database).Read());
        Assert.False(File.Exists(fixture.Effects.Path));
        Assert.False(fixture.Service.Read().Effective.AutoStart);
    }

    [Theory(DisplayName = "Rejected Startup Preparation Or Save Cannot Change Settings Or Native Registration")]
    [InlineData("validate")]
    [InlineData("elevation")]
    [InlineData("revision")]
    [InlineData("sql")]
    public void RejectsFailedPreparation(string fault)
    {
        using var fixture = new Fixture();
        var store = new HostSettingsStore(fixture.Database);
        if (fault == "revision")
        {
            var newer = new HostSettingsChange(0, HostSettings.Default with { HistoryRetentionDays = 20 });
            fixture.Service.Save(Identity(newer), newer, DateTimeOffset.UtcNow);
        }
        var initial = store.Read();
        fixture.Effects.FailValidation = fault == "validate";
        fixture.Effects.RequireElevation = fault == "elevation";
        fixture.Effects.BeforeMutation = RejectMutation;
        if (fault == "sql")
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Database.DatabasePath, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fixture_reject_settings BEFORE INSERT ON settings_snapshots WHEN NEW.revision>0 BEGIN SELECT RAISE(ABORT,'fixture_write_failure'); END;";
            command.ExecuteNonQuery();
        }
        var change = new HostSettingsChange(0, initial.Values with { AutoStart = true });
        var operation = Identity(change);
        var failure = Record.Exception(() => fixture.Service.Save(operation, change, DateTimeOffset.UtcNow));
        switch (fault)
        {
            case "validate": Assert.Equal("fixture_validate_failed", Assert.IsType<IOException>(failure).Message); break;
            case "elevation": Assert.Equal("startup_registration_elevation_required", Assert.IsType<HostSettingsException>(failure).Message); break;
            case "revision": Assert.Equal("settings_revision_conflict", Assert.IsType<HostSettingsException>(failure).Message); break;
            case "sql": Assert.Contains("fixture_write_failure", Assert.IsType<SqliteException>(failure).Message, StringComparison.Ordinal); break;
        }
        Assert.Null(store.Lookup(operation, change));
        Assert.Equal(initial, store.Read());
        Assert.Equal(initial, fixture.Service.Read().Saved);
        Assert.False(fixture.Service.Read().Effective.AutoStart);
        Assert.False(File.Exists(fixture.Effects.Path));
        Assert.Empty(fixture.Index.Current.ResourceHolds);
    }

    [Fact(DisplayName = "An Unknown Startup Effect Remains Saved And Isolated Without Being Replayed While Its Creator Lives")]
    public void PreservesUnknownEffects()
    {
        using var fixture = new Fixture();
        fixture.Effects.ThrowAfterCreate = true;
        var change = new HostSettingsChange(0, HostSettings.Default with { AutoStart = true });
        var operation = Identity(change);
        var result = fixture.Service.Save(operation, change, DateTimeOffset.UtcNow);
        Assert.Equal(new StartupEffectStatus(operation.OperationId, 1, true, StartupEffectState.Quarantined), result.StartupEffect);
        Assert.True(result.Saved.Values.AutoStart);
        Assert.False(fixture.Service.Read().Effective.AutoStart);
        Assert.Equal(StartupHolds(operation.OperationId), fixture.Index.Current.ResourceHolds);
        var task = PhysicalObjectIdentity.Read(fixture.Effects.Path);
        var taskBytes = File.ReadAllBytes(fixture.Effects.Path);
        fixture.Effects.BeforeMutation = RejectMutation;
        Assert.Equal(result with { IsNew = false }, fixture.Service.Save(operation, change, DateTimeOffset.UtcNow));
        var before = DatabaseBytes(fixture.Database.DatabasePath);
        Assert.Equal("startup_effect_creator_not_stopped", Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Instance,
            System.IO.Path.Combine(fixture.Root, "diagnostics"), new JournalStartupAdmission(fixture.Instance, new()))).Message);
        Assert.Equal(before, DatabaseBytes(fixture.Database.DatabasePath));
        Assert.Equal(task, PhysicalObjectIdentity.Read(fixture.Effects.Path));
        Assert.Equal(taskBytes, File.ReadAllBytes(fixture.Effects.Path));
        Assert.Equal("recovery_pending", fixture.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).ReasonCode);
    }

    [Fact(DisplayName = "Pending Startup Intent Rejects Settings Writes Even If Its Runtime Projection Has Not Been Published")]
    public void BlocksPendingWrites()
    {
        using var fixture = new Fixture();
        fixture.Effects.ThrowAfterCreate = true;
        var change = new HostSettingsChange(0, HostSettings.Default with { AutoStart = true });
        var saved = fixture.Service.Save(Identity(change), change, DateTimeOffset.UtcNow).Saved;
        fixture.Effects.BeforeMutation = RejectMutation;
        fixture.Index.UpdateResourceHolds(RecoveryHoldOwner.StartupRegistration, []);
        var next = new HostSettingsChange(1, change.Values with { HistoryRetentionDays = 20 });
        var operation = Identity(next);
        Assert.Equal("settings_startup_recovery_required", Assert.Throws<HostSettingsException>(() =>
            fixture.Service.Save(operation, next, DateTimeOffset.UtcNow)).Message);
        var store = new HostSettingsStore(fixture.Database);
        Assert.Null(store.Lookup(operation, next));
        Assert.Equal(saved, store.Read());
    }

    private static void RejectMutation() => throw new InvalidOperationException("Unexpected external startup mutation");

    internal static RecoveryResourceHold[] StartupHolds(Guid operationId) =>
        new[] { "settings:host", "system:startup" }.Select(key => new RecoveryResourceHold(operationId, key, "startup_effect_pending")
            { Owner = RecoveryHoldOwner.StartupRegistration, Mode = ResourceClaimMode.Mutation }).ToArray();

    private static byte[] DatabaseBytes(string path) => new[] { "", "-wal", "-shm" }
        .Where(suffix => File.Exists(path + suffix)).SelectMany(suffix => File.ReadAllBytes(path + suffix)).ToArray();
    private static OperationIdentity Identity(HostSettingsChange change) => HostSettingsStore.Identity(Guid.NewGuid(), "fixture", change);

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; }
        internal RuntimeInstance Instance { get; }
        internal StateDatabase Database { get; }
        internal InstanceLease Lease { get; }
        internal RecoveryIndex Index { get; }
        internal HostLifecycle Lifecycle { get; }
        internal ResourceLeaseManager Resources { get; }
        internal ControlledStartupRegistration Effects { get; }
        internal HostSettingsService Service { get; }

        internal Fixture()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            Root = System.IO.Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "startup-settings-" + Guid.NewGuid().ToString("N"));
            Instance = new InstanceRoot(new()).OpenOrCreate(System.IO.Path.Combine(Root, "runtime"));
            Database = StateDatabase.CreateFresh(Instance);
            Lease = InstanceLease.Acquire(Instance, new());
            Index = new StartupJournalRecovery(Instance, new(), Database, new(Root, System.IO.Path.Combine(Instance.RuntimeRoot, "extensions"))).Recover(Lease);
            Lifecycle = new(Index, TimeProvider.System);
            Resources = new(Lifecycle);
            Effects = new(System.IO.Path.Combine(Root, "controlled-task.xml"));
            var executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
            Service = HostStartupSettings.CreateForInstallation(Instance, Database, Lease, Resources, Index, executable, Effects);
        }
        public void Dispose() => Lease.Dispose();
    }

}
