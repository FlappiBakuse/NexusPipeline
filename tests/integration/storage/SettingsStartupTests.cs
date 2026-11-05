using System.Security.Cryptography;
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
        fixture.Effects.BeforeCreate = () =>
        {
            Assert.True(new HostSettingsStore(fixture.Database).Read().Values.AutoStart);
            Assert.True(fixture.Service.Read().Saved.Values.AutoStart);
            Assert.False(fixture.Service.Read().Effective.AutoStart);
            Assert.Equal(2, fixture.Index.Current.ResourceHolds.Count);
            using var unrelated = fixture.Resources.TryAcquire(Guid.NewGuid(), [new("unrelated", ResourceClaimMode.Mutation)]).Lease;
            Assert.NotNull(unrelated);
        };
        var change = new HostSettingsChange(0, HostSettings.Default with { AutoStart = true });
        var operation = Identity(change);
        var enabled = fixture.Service.Save(operation, change, DateTimeOffset.UtcNow);
        Assert.Equal(StartupEffectState.Completed, enabled.StartupEffect!.State);
        Assert.True(fixture.Service.Read().Effective.AutoStart);
        Assert.Empty(fixture.Index.Current.ResourceHolds);
        var disable = new HostSettingsChange(1, change.Values with { AutoStart = false });
        var disabled = fixture.Service.Save(Identity(disable), disable, DateTimeOffset.UtcNow);
        Assert.Equal(StartupEffectState.Completed, disabled.StartupEffect!.State);
        Assert.False(fixture.Service.Read().Effective.AutoStart);
        Assert.False(File.Exists(fixture.Effects.Path));
        Assert.False(fixture.Service.Save(operation, change, DateTimeOffset.UtcNow).IsNew);
        Assert.Equal(1, fixture.Effects.Creates);
        Assert.Equal(1, fixture.Effects.Deletes);
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
        fixture.Effects.FailValidation = fault == "validate";
        fixture.Effects.RequireElevation = fault == "elevation";
        if (fault == "sql")
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Database.DatabasePath, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fixture_reject_settings BEFORE INSERT ON settings_snapshots WHEN NEW.revision>0 BEGIN SELECT RAISE(ABORT,'fixture_write_failure'); END;";
            command.ExecuteNonQuery();
        }
        var change = new HostSettingsChange(fault == "revision" ? 1 : 0, HostSettings.Default with { AutoStart = true });
        var failure = Record.Exception(() => fixture.Service.Save(Identity(change), change, DateTimeOffset.UtcNow));
        Assert.NotNull(failure);
        if (fault == "elevation") Assert.Equal("startup_registration_elevation_required", Assert.IsType<HostSettingsException>(failure).Message);
        Assert.Equal(0, new HostSettingsStore(fixture.Database).Read().Revision);
        Assert.False(fixture.Service.Read().Saved.Values.AutoStart);
        Assert.False(fixture.Service.Read().Effective.AutoStart);
        Assert.Equal(0, fixture.Effects.Creates);
        Assert.Equal(0, fixture.Effects.Deletes);
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
        Assert.Equal(StartupEffectState.Quarantined, result.StartupEffect!.State);
        Assert.True(result.Saved.Values.AutoStart);
        Assert.False(fixture.Service.Read().Effective.AutoStart);
        Assert.Equal(2, fixture.Index.Current.ResourceHolds.Count);
        Assert.Equal(result with { IsNew = false }, fixture.Service.Save(operation, change, DateTimeOffset.UtcNow));
        Assert.Equal(1, fixture.Effects.Creates);
        var before = DatabaseBytes(fixture.Database.DatabasePath);
        Assert.Throws<IOException>(() => StateDatabase.OpenExisting(fixture.Instance,
            System.IO.Path.Combine(fixture.Root, "diagnostics"), new JournalStartupAdmission(fixture.Instance, new())));
        Assert.Equal(before, DatabaseBytes(fixture.Database.DatabasePath));
        Assert.Equal("recovery_pending", fixture.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid()).ReasonCode);
    }

    [Fact(DisplayName = "Pending Startup Intent Rejects Settings Writes Even If Its Runtime Projection Has Not Been Published")]
    public void BlocksPendingWrites()
    {
        using var fixture = new Fixture();
        fixture.Effects.ThrowAfterCreate = true;
        var change = new HostSettingsChange(0, HostSettings.Default with { AutoStart = true });
        fixture.Service.Save(Identity(change), change, DateTimeOffset.UtcNow);
        fixture.Index.UpdateResourceHolds(RecoveryHoldOwner.StartupRegistration, []);
        var next = new HostSettingsChange(1, change.Values with { HistoryRetentionDays = 20 });
        Assert.Equal("settings_startup_recovery_required", Assert.Throws<HostSettingsException>(() =>
            fixture.Service.Save(Identity(next), next, DateTimeOffset.UtcNow)).Message);
        Assert.Equal(1, new HostSettingsStore(fixture.Database).Read().Revision);
        Assert.Equal(1, fixture.Effects.Creates);
    }

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
            var template = new StartupRegistrationCommand(new(Instance.InstanceId, Guid.NewGuid(), ProcessIdentityReader.Read(Environment.ProcessId).UserSid,
                executable, ["--fixture-only"], Root, DateTimeOffset.UtcNow), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable))));
            var startup = new StartupRegistrationCoordinator(Instance, Database, Lease, Index, Effects, template);
            var defaults = HostSettings.Default;
            Service = new(Instance, Database, Lease, Resources, new(defaults.Desktop.Mode, defaults.Service, defaults.Updates.PluginSource, false), startup);
        }
        public void Dispose() => Lease.Dispose();
    }

}
