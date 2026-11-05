using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallationCommitTests
{
    [Theory(DisplayName = "Actual Installed Payload Commits With The Saved Preference And Only The Real Published Scope Is Enabled")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitsPublishedScopes(bool disabled)
    {
        using var fixture = new InstallRuntimeFixture(disabled ? false : null);
        var before = new HostSettingsStore(fixture.Package.Catalog.Database).Read();
        var result = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        Assert.True(result.IsInstalled); Assert.Equal(!disabled, result.IsEnabled);
        var bound = fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan);
        Assert.Equal(JournalPhase.Committed, bound.Registered.Phase);
        Assert.Equal(disabled ? "not-attempted" : "published", bound.Commit!.ActivationState);
        Assert.Equal(disabled, bound.Preparation is null);
        var after = new HostSettingsStore(fixture.Package.Catalog.Database).Read();
        Assert.Equal(before.Revision + (disabled ? 0 : 1), after.Revision);
        Assert.Equal(!disabled, Assert.Single(after.Values.Extensions).Enabled);
        var installed = new ExtensionInstalledEvidence(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Database,
            fixture.Package.Catalog.Contracts, fixture.Roots).ReadCurrent("fixture-plugin")!;
        Assert.Equal(1, installed.Metadata.Revision);
        Assert.Equal(bound.Physical[^1].Record.CandidateObjects.ToArray(), installed.Footprint.Payload.OriginalObjects.ToArray());
        Assert.Equal(PhysicalObjectIdentity.Read(fixture.Target),
            Assert.Single(installed.Footprint.Payload.OriginalObjects, item => item.RelativePath == "").Identity);
        var replay = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        Assert.Equal(result, replay);
        using var stopped = await fixture.Activator.ReserveForApplyAsync(fixture.Package.OperationId, "fixture-plugin", default);
        Assert.False(result.ActivationId is { } id && fixture.Activator.IsPublished("fixture-plugin", id));
    }

    [Fact(DisplayName = "A Final Sql Failure Keeps Both Old Settings And Installation Rows And Replays Only The Original Published Candidate")]
    public async Task PreservesFailedCommit()
    {
        using var fixture = new InstallRuntimeFixture();
        var before = new HostSettingsStore(fixture.Package.Catalog.Database).Read();
        using var inventory = await fixture.Activator.ReserveForApplyAsync(fixture.Package.OperationId, "fixture-plugin", default);
        _ = fixture.Physical().Apply(fixture.Prepared, inventory, default);
        await using var source = PreparedExtensionActivation.Prepare(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Database,
            fixture.Package.Catalog.Lease, fixture.Package.Catalog.Contracts, fixture.Roots, fixture.Prepared, inventory, default);
        await using var activation = await inventory.PrepareCandidateAsync(source);
        var store = fixture.Committer();
        var preparation = store.PrepareActivation(fixture.Prepared, inventory, activation, default);
        activation.Publish(preparation);
        fixture.Package.Catalog.Sql("CREATE TRIGGER fixture_commit_failure BEFORE UPDATE OF phase ON operation_journals " +
            "WHEN NEW.operation_kind='extensionApply' AND NEW.phase='committed' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        try { Assert.Throws<SqliteException>(() => store.Commit(fixture.Prepared, inventory, activation, default)); }
        finally { fixture.Package.Catalog.Sql("DROP TRIGGER fixture_commit_failure;"); }
        Assert.True(System.Text.Json.JsonElement.DeepEquals(System.Text.Json.JsonSerializer.SerializeToElement(before),
            System.Text.Json.JsonSerializer.SerializeToElement(new HostSettingsStore(fixture.Package.Catalog.Database).Read())));
        using (var connection = DatabaseConnections.Open(fixture.Package.Catalog.Database.DatabasePath, readOnly: true))
        using (var transaction = connection.BeginTransaction(deferred: true))
            Assert.Null(ExtensionInstalledRows.ReadCurrentMetadata(connection, transaction, "fixture-plugin"));
        var physical = new JournalStore(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Contracts).Read(fixture.Accepted.JournalId);
        Assert.Equal(JournalPhase.Committed, physical.Phase);
        Assert.Equal(JournalPhase.Applied, new RecoveryJournalRegistry(fixture.Package.Catalog.Database).ReadJournal(fixture.Accepted.JournalId).Phase);
        var target = PhysicalObjectIdentity.Read(fixture.Target);
        var result = store.Commit(fixture.Prepared, inventory, activation, default);
        _ = activation.CompleteCommitted();
        Assert.Equal(activation.ActivationId, result.Commit!.ActivationId);
        Assert.Equal(target, PhysicalObjectIdentity.Read(fixture.Target));
        Assert.Equal(physical.Export(), new JournalStore(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Contracts)
            .Read(fixture.Accepted.JournalId).Export());
        Assert.Equal(before.Revision + 1, new HostSettingsStore(fixture.Package.Catalog.Database).Read().Revision);
        inventory.Dispose();
        using var stopped = await fixture.Activator.ReserveForApplyAsync(fixture.Package.OperationId, "fixture-plugin", default);
    }

    [Fact(DisplayName = "A New Caller Reads The Original Completed Acceptance Before Any New Package Preparation")]
    public async Task ReadsCompletedAcceptance()
    {
        using var fixture = new InstallRuntimeFixture(savedEnabled: false);
        _ = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        var source = fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan).Source;
        var bytes = File.ReadAllBytes(Path.Combine(fixture.Target, "manifest.json"));
        var target = PhysicalObjectIdentity.Read(fixture.Target);
        fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose();
        fixture.Package.Publish(2);
        var result = new PayloadAcceptanceLookup(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Database,
            fixture.Package.Catalog.Contracts, fixture.Roots).ReadAccepted(fixture.Package.OperationId, "local",
                ExtensionApplyRequest.FromSource(source));
        Assert.NotNull(result); Assert.Equal(fixture.Accepted.JournalId, result.JournalId);
        Assert.Equal(fixture.Accepted.Plan, result.Plan);
        Assert.Equal(target, PhysicalObjectIdentity.Read(fixture.Target));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.Target, "manifest.json")));
        Assert.Equal(JournalPhase.Committed, fixture.Binding().Read(result.JournalId, result.Plan).Registered.Phase);
    }

    [Fact(DisplayName = "A Completed Callback And Persisted Preparation Still Cannot Commit Before The Actual Publish")]
    public async Task RequiresActualPublication()
    {
        using var fixture = new InstallRuntimeFixture();
        using var inventory = await fixture.Activator.ReserveForApplyAsync(fixture.Package.OperationId, "fixture-plugin", default);
        _ = fixture.Physical().Apply(fixture.Prepared, inventory, default);
        await using var source = PreparedExtensionActivation.Prepare(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Database,
            fixture.Package.Catalog.Lease, fixture.Package.Catalog.Contracts, fixture.Roots, fixture.Prepared, inventory, default);
        await using var activation = await inventory.PrepareCandidateAsync(source);
        var store = fixture.Committer();
        _ = store.PrepareActivation(fixture.Prepared, inventory, activation, default);
        Assert.False(fixture.Activator.IsPublished("fixture-plugin", activation.ActivationId));
        Assert.Throws<IOException>(() => store.Commit(fixture.Prepared, inventory, activation, default));
        var bound = fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan);
        Assert.NotNull(bound.Preparation); Assert.Null(bound.Commit); Assert.Equal(JournalPhase.Applied, bound.Registered.Phase);
        Assert.Empty(new HostSettingsStore(fixture.Package.Catalog.Database).Read().Values.Extensions);
    }
}
