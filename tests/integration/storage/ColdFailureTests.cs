using System.Runtime.Versioning;
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
public sealed class ColdFailureTests
{
    [Theory(DisplayName = "A Real Known Callback Failure Prefix Can Resume Only After The Actual Original Owner And Whole Family Stop")]
    [InlineData("started")]
    [InlineData("restored")]
    public async Task RequiresStoppedFamily(string stage)
    {
        await using var fixture = new InstallationCrashFixture("failed-restoration", PayloadAction.Upgrade, stage);
        await fixture.StartAsync();
        var before = new ExtensionPayloadBinding(fixture.Instance, fixture.Package.Catalog.Database,
            fixture.Package.Catalog.Contracts, fixture.Roots).Read(fixture.JournalId, fixture.Plan);
        var failure = before.Restoration[0].Record.Failure;
        Assert.NotNull(failure); Assert.Equal(fixture.Native.ProcessId, failure.Writer.Process.ProcessId);
        Assert.Equal(fixture.Native.CreatedAtUtc, failure.Writer.Process.CreatedAtUtc);
        Assert.Equal(stage, before.Restoration[^1].Record.Stage);
        Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "failed", "activated.json")));
        Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "failed", "cleaned.json")));
        using (var released = File.Open(Path.Combine(fixture.ActivationDirectory, "failed", "resource.bin"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.True(released.Length > 0);
        Assert.Equal(WriterState.Running, WriterStopEvidence.ObserveProcess(failure.Writer.Process));
        Assert.Throws<IOException>(() => new JournalRecoveryOwnership(fixture.Package.Catalog.Database)
            .VerifyStopped([before.Registered]));
        var bytes = before.History.Select(item => item.Export()).ToArray();
        var stopped = await fixture.StopAsync();
        Assert.True(stopped.CompleteJobStopped);
        Assert.Contains(stopped.Family, item => item.ProcessId == fixture.Native.ProcessId && item.CreatedAtUtc == fixture.Native.CreatedAtUtc);
        Assert.All(stopped.Family, item => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(item)));
        using var lease = InstanceLease.Acquire(fixture.Instance, fixture.Package.Catalog.Contracts);
        var database = StateDatabase.OpenExisting(fixture.Instance, Path.Combine(fixture.Package.Root, "inspection"),
            new JournalStartupAdmission(fixture.Instance, fixture.Package.Catalog.Contracts, fixture.Roots));
        var settings = new HostSettingsStore(database).Read();
        var index = new StartupJournalRecovery(fixture.Instance, fixture.Package.Catalog.Contracts, database, fixture.Roots).Recover(lease);
        Assert.True(index.Current.IsReady);
        var after = Read();
        Assert.Equal(JournalPhase.Committed, after.Registered.Phase); Assert.Null(after.Commit);
        Assert.Equal("committed", after.Restoration[^1].Record.Stage);
        Assert.All(after.Restoration, entry => Assert.Equal(failure, entry.Record.Failure));
        Assert.Equal("reverted", after.Latest.Root.GetProperty("receipts")[0].GetProperty("effect").GetString());
        var scanner = new FileTreeScanner(new(4096, 268435456));
        var actual = scanner.Capture(after.Plan.TargetPath, default);
        Assert.Equal(after.Plan.Before.Snapshot.Entries, actual.Entries);
        Assert.Equal(after.Plan.Before.OriginalObjects, PhysicalTreeIdentity.Capture(after.Plan.TargetPath, actual));
        Assert.Equal(settings.Revision, new HostSettingsStore(database).Read().Revision);
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
            Assert.Equal(1, ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, "fixture-plugin")!.Revision);
        for (var i = 0; i < bytes.Length; i++) Assert.Equal(bytes[i], after.History[i].Export());
        var final = after.Latest.Export();
        _ = new StartupJournalRecovery(fixture.Instance, fixture.Package.Catalog.Contracts, database, fixture.Roots).Recover(lease);
        Assert.Equal(final, Read().Latest.Export());

        BoundExtensionPayload Read() => new ExtensionPayloadBinding(fixture.Instance, database,
            fixture.Package.Catalog.Contracts, fixture.Roots).Read(fixture.JournalId, fixture.Plan);
    }
}
