using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ActivationFailureTests
{
    [Fact(DisplayName = "Invalid Provider Inputs Are Rejected Before Extension Entry And Restore The Original Owned Tree")]
    public async Task RejectsInvalidProvider()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "return",
            managedManifestEdit: manifest =>
            {
                manifest["capabilities"] = new System.Text.Json.Nodes.JsonArray("execution.drive");
                manifest["permissions"] = new System.Text.Json.Nodes.JsonObject
                    { ["processAccess"] = new System.Text.Json.Nodes.JsonArray("owned-worker", "selected-upstream") };
                manifest["execution"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["entryId"] = "main", ["workerEntry"] = "empty.exe", ["arguments"] = new System.Text.Json.Nodes.JsonArray(),
                    ["protocol"] = "nexus.execution.provider/1", ["configurationResources"] = new System.Text.Json.Nodes.JsonArray()
                };
            }, managedPayloadEdit: directory => File.WriteAllBytes(Path.Combine(directory, "empty.exe"), []),
            providers: new NexusPipeline.Platform.Extensions.ExecutionProviderRegistry());
        var failure = await Assert.ThrowsAsync<IOException>(async () => await fixture.Session().ApplyAsync(fixture.Prepared, default));
        Assert.Equal("judgement_interpreter_size_invalid", failure.Message);
        Assert.False(File.Exists(Path.Combine(fixture.ActivationDirectory, "activated.json")));
        Assert.Empty(fixture.Activator.ReadPublishedScopes());
        var bound = fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan);
        Assert.Equal(JournalPhase.Committed, bound.Registered.Phase); Assert.Null(bound.Commit);
        Assert.Equal("committed", bound.Restoration[^1].Record.Stage);
        Assert.False(Directory.Exists(fixture.Target));
    }

    [Fact(DisplayName = "A Real Upgrade Callback Failure Restores The Original Owned Tree And Preference Before Explicit Reactivation")]
    public async Task RestoresFailedUpgrade()
    {
        await using var fixture = await ExtensionUpgradeFixture.CreateAsync(enabled: true, nextMode: "throw");
        var owner = fixture.Original.Package.Catalog;
        var original = fixture.Original.Binding().Read(fixture.Original.Accepted.JournalId, fixture.Original.Accepted.Plan);
        var settings = new HostSettingsStore(owner.Database).Read();
        fixture.Prepare(PayloadAction.Upgrade); fixture.Accept();
        await Assert.ThrowsAnyAsync<Exception>(async () => await fixture.Session().ApplyAsync(fixture.Prepared!, default));
        var bound = fixture.Bound();
        Assert.Equal(JournalPhase.Committed, bound.Registered.Phase); Assert.Null(bound.Commit);
        Assert.Equal("committed", bound.Restoration[^1].Record.Stage);
        var failure = bound.Restoration[0].Record.Failure;
        Assert.NotNull(failure); Assert.NotEqual(Guid.Empty, failure.ActivationId);
        Assert.Equal(JournalWriterStore.CurrentWriter, failure.Writer);
        Assert.True(File.Exists(Path.Combine(fixture.NextActivationDirectory, "activated.json")));
        Assert.True(File.Exists(Path.Combine(fixture.NextActivationDirectory, "cleaned.json")));
        using (var resource = File.Open(Path.Combine(fixture.NextActivationDirectory, "resource.bin"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.True(resource.Length > 0);
        var actual = fixture.Scan(fixture.Original.Target);
        Assert.Equal(bound.Plan.Before.Snapshot.Entries, actual.Entries);
        Assert.Equal(bound.Plan.Before.OriginalObjects, PhysicalTreeIdentity.Capture(fixture.Original.Target, actual));
        using (var connection = DatabaseConnections.Open(owner.Database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content))
        using (var transaction = connection.BeginTransaction(deferred: true))
            Assert.Equal(original.Commit!.InstalledRevision,
                ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, "fixture-plugin")!.Revision);
        var currentSettings = new HostSettingsStore(owner.Database).Read();
        Assert.Equal(settings.Revision, currentSettings.Revision);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(settings), JsonSerializer.SerializeToElement(currentSettings)));
        Assert.Empty(fixture.Original.Activator.ReadPublishedScopes());
        Assert.All(new FileEffectOutbox(owner.Database).Read(bound.Plan.JournalId)
            .Where(item => item.Action is FileEffectAction.PreserveOriginal or FileEffectAction.InstallCandidate),
            item => Assert.Equal(FileEffectState.Completed, item.State));
        var saved = bound.Latest.Export();
        fixture.Prepared!.Dispose(); fixture.Verified!.Dispose(); fixture.Root!.Dispose();
        foreach (var filename in new[] { "activated.json", "cleaned.json", "resource.bin" })
            File.Delete(Path.Combine(fixture.Original.ActivationDirectory, filename));
        var enabled = await new InstalledActivationService(owner.Instance, owner.Database, owner.Lease,
            owner.Contracts, fixture.Original.Roots, owner.Resources, owner.Index, owner.Store, fixture.Original.Activator)
            .ActivateAsync("fixture-plugin", default);
        Assert.Equal(1, enabled.InstalledRevision);
        Assert.True(fixture.Original.Activator.IsPublished("fixture-plugin", enabled.ActivationId));
        Assert.Equal(saved, fixture.Bound().Latest.Export());
    }

    [Fact(DisplayName = "A Real Failed Cleanup Cannot Authorize Restoration Even After Its Managed File Handle Was Closed")]
    public async Task RequiresCompletedCleanup()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "throw-cleanup");
        await Assert.ThrowsAnyAsync<Exception>(async () => await fixture.Session().ApplyAsync(fixture.Prepared, default));
        Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "activated.json")));
        Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "cleaned.json")));
        using (var closed = File.Open(Path.Combine(fixture.ActivationDirectory, "resource.bin"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.True(closed.Length > 0);
        var bound = fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan);
        Assert.Equal(JournalPhase.Applied, bound.Registered.Phase);
        Assert.Empty(bound.Restoration); Assert.Null(bound.Preparation); Assert.Null(bound.Commit);
        Assert.True(Directory.Exists(fixture.Target));
        Assert.Throws<IOException>(() => new JournalRecoveryOwnership(fixture.Package.Catalog.Database)
            .VerifyStopped([bound.Registered]));
    }

    [Fact(DisplayName = "A Timed Out Actual Callback Cannot Later Mint The Known Failure Restoration Authority")]
    public async Task RejectsLateCallbacks()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "late");
        var running = fixture.Session().ApplyAsync(fixture.Prepared, default).AsTask();
        try
        {
            _ = await OwnedCrashProcess.ReadReportAsync(Path.Combine(fixture.ActivationDirectory, "activated.json"));
            await Assert.ThrowsAnyAsync<Exception>(async () => await running.WaitAsync(TimeSpan.FromSeconds(25)));
            Assert.True(running.IsCompleted);
            var pending = fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan);
            Assert.Equal(JournalPhase.Applied, pending.Registered.Phase); Assert.Empty(pending.Restoration);
        }
        finally
        {
            File.WriteAllText(Path.Combine(fixture.ActivationDirectory, "release"), "release");
            await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(5));
        }
        var after = fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan);
        Assert.Equal(JournalPhase.Applied, after.Registered.Phase); Assert.Empty(after.Restoration);
        Assert.Null(after.Commit); Assert.True(Directory.Exists(fixture.Target));
    }

    [Fact(DisplayName = "A Failed First Restoration Sql Projection Keeps The Known Failure Prefix And Does Not Replay The Callback")]
    public async Task KeepsFailedProjection()
    {
        await using var fixture = await ExtensionUpgradeFixture.CreateAsync(enabled: true, nextMode: "throw");
        var owner = fixture.Original.Package.Catalog;
        fixture.Prepare(PayloadAction.Upgrade); fixture.Accept();
        Sql("CREATE TRIGGER main.fixture_restore_fail BEFORE UPDATE OF phase ON operation_journals " +
            "WHEN NEW.journal_id='" + fixture.Accepted!.JournalId.ToString("D") +
            "' AND NEW.phase='reverting' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(async () => await fixture.Session().ApplyAsync(fixture.Prepared!, default));
        }
        finally { Sql("DROP TRIGGER main.fixture_restore_fail;"); }
        var bound = fixture.Bound();
        Assert.Equal(JournalPhase.Applied, bound.Registered.Phase); Assert.Equal(JournalPhase.Reverting, bound.Latest.Phase);
        Assert.Equal("started", Assert.Single(bound.Restoration).Record.Stage);
        Assert.NotNull(bound.Restoration[0].Record.Failure);
        Assert.Equal(bound.Plan.Before.Snapshot.Entries, fixture.Scan(fixture.Backup(bound)).Entries);
        Assert.Equal(bound.Plan.Candidate.Snapshot.Entries, fixture.Scan(fixture.Original.Target).Entries);
        var activated = File.ReadAllBytes(Path.Combine(fixture.NextActivationDirectory, "activated.json"));
        var journal = bound.Latest.Export();
        Assert.Throws<IOException>(() => new JournalRecoveryOwnership(owner.Database).VerifyStopped([bound.Registered]));
        Assert.Equal(journal, fixture.Bound().Latest.Export());
        Assert.Equal(activated, File.ReadAllBytes(Path.Combine(fixture.NextActivationDirectory, "activated.json")));

        void Sql(string sql)
        {
            using var connection = DatabaseConnections.Open(owner.Database.DatabasePath, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
            using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
    }
}
