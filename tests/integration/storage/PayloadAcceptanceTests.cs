using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PayloadAcceptanceTests
{
    [Theory(DisplayName = "Opaque Acceptance Freezes The Actual Preference And Claims Without Installing Or Activating")]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void FreezesAcceptedPreference(bool? enabled)
    {
        using var fixture = new PayloadAcceptanceFixture(enabled, mutation: enabled is null);
        var original = new HostSettingsStore(fixture.Package.Catalog.Database).Read();
        _ = fixture.Prepare();
        var accepted = fixture.Accept(); var source = fixture.Source(accepted);
        Assert.Equal(enabled ?? true, source.DesiredEnabled);
        Assert.Equal(enabled is null, source.InitializeEnabledPreference);
        Assert.Equal(original.Revision, source.SettingsRevision);
        Assert.Equal(enabled is null ? ResourceClaimMode.Mutation : ResourceClaimMode.Reference,
            Assert.Single(source.Claims, claim => claim.ResourceKey == "settings:host").Mode);
        var claims = JournalResourceClaims.Read(fixture.Package.Catalog.Database, accepted.JournalId);
        Assert.Equal(source.Claims.Length, claims.Count);
        Assert.All(source.Claims, claim => Assert.Equal(claim.Mode, claims[claim.ResourceKey]));
        Assert.Equal(BoundedJson.Serialize(original), BoundedJson.Serialize(new HostSettingsStore(fixture.Package.Catalog.Database).Read()));
        Assert.False(Directory.Exists(fixture.Target));
        var replay = fixture.Accept();
        Assert.False(replay.Acceptance.IsNew); Assert.Equal(accepted.Plan, replay.Plan);
    }

    [Fact(DisplayName = "Extension Claim Inventory Rejects Changed Sql Modes And Preserves Its Frozen Plan And Source")]
    public void RechecksExtensionClaims()
    {
        using var fixture = new PayloadAcceptanceFixture();
        _ = fixture.Prepare(); var accepted = fixture.Accept();
        var catalog = fixture.Package.Catalog;
        var plan = new PayloadPlanCodec(catalog.Instance, catalog.Contracts, fixture.Package.Blobs, fixture.Roots)
            .ReadStructure(accepted.Plan, out var source);
        Assert.NotNull(source);
        var planPath = Path.Combine(catalog.Instance.RuntimeRoot, accepted.Plan.RelativePath);
        var sourcePath = Path.Combine(catalog.Instance.RuntimeRoot, plan.ExtensionSource!.RelativePath);
        var planBytes = File.ReadAllBytes(planPath); var sourceBytes = File.ReadAllBytes(sourcePath);
        JournalResourceClaims.VerifyInventory(catalog.Instance, catalog.Database, catalog.Contracts, fixture.Roots);
        catalog.Database.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE main.journal_resource_claims SET mode=$mode WHERE journal_id=$journal AND resource_key='settings:host';";
            command.Parameters.AddWithValue("$mode", (int)ResourceClaimMode.Reference);
            command.Parameters.AddWithValue("$journal", accepted.JournalId.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
            return true;
        });
        var error = Assert.Throws<IOException>(() => JournalResourceClaims.VerifyInventory(
            catalog.Instance, catalog.Database, catalog.Contracts, fixture.Roots));
        Assert.Equal("journal_resource_claims_unbound", error.Message);
        Assert.Equal(planBytes, File.ReadAllBytes(planPath)); Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));
    }

    [Fact(DisplayName = "Protected Gate And Registry Reads Observe Commits Even Inside Another Read Transaction")]
    public void ReadsCurrentRecovery()
    {
        using var fixture = new PayloadAcceptanceFixture();
        _ = fixture.Prepare(); var accepted = fixture.Accept();
        var catalog = fixture.Package.Catalog; var database = catalog.Database;
        var registry = new RecoveryJournalRegistry(database); var gate = new JournalEffectGate(database);
        using var history = new HistoryReadScope(catalog.Instance, database, catalog.Contracts);
        var before = registry.ReadSnapshot(accepted.JournalId);
        var journal = Assert.Single(before.Journals);
        Assert.Equal(EffectGateState.Unentered, gate.Read(accepted.JournalId).State);
        var currentRevision = HistoryReadScope.ReadDatabase(database, (connection, transaction) =>
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT revision FROM main.recovery_index_state WHERE singleton=1;";
            Assert.Equal(before.Revision, (long)command.ExecuteScalar()!);
            Assert.Equal(EffectGateState.Entered, gate.Enter(journal, 1).State);
            Assert.Equal(EffectGateState.Entered, gate.Read(accepted.JournalId).State);
            var after = registry.ReadSnapshot(accepted.JournalId);
            Assert.True(after.Revision > before.Revision);
            Assert.Equal(before.Revision, (long)command.ExecuteScalar()!);
            return after.Revision;
        });
        Assert.Equal(currentRevision, registry.ReadSnapshot(accepted.JournalId).Revision);
        Assert.Equal(EffectGateState.Entered, gate.Read(accepted.JournalId).State);
        Assert.Throws<IOException>(() => HistoryReadScope.ReadDatabase<int>(database,
            (_, _) => throw new IOException("controlled_history_read_failure")));
        Assert.Equal(currentRevision, registry.ReadSnapshot(accepted.JournalId).Revision);
    }

    [Fact(DisplayName = "A First Preference Cannot Be Accepted Under A Reference Lease")]
    public void RequiresMutationLease()
    {
        using var fixture = new PayloadAcceptanceFixture(mutation: false);
        var count = fixture.Package.Catalog.Count("operations");
        Assert.Throws<InvalidOperationException>(() => fixture.Prepare());
        Assert.Equal(count, fixture.Package.Catalog.Count("operations"));
        Assert.False(Directory.Exists(fixture.Target));
    }

    [Theory(DisplayName = "Changed Actual Authority Is Rejected Before An Operation Is Accepted")]
    [InlineData("settings")]
    [InlineData("catalog")]
    [InlineData("physical")]
    [InlineData("recovery")]
    public void RechecksActualAuthority(string changed)
    {
        using var fixture = new PayloadAcceptanceFixture(catalogHistory: changed == "catalog");
        _ = fixture.Prepare();
        if (changed == "settings")
        {
            var store = new HostSettingsStore(fixture.Package.Catalog.Database); var before = store.Read();
            var change = new HostSettingsChange(before.Revision, before.Values with { Extensions = [new("fixture-plugin", false)] });
            _ = store.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "local", change), change, DateTimeOffset.UtcNow);
        }
        else if (changed == "catalog") fixture.Package.Catalog.Sql("UPDATE extensions.extension_catalog_heads SET sequence=1," +
            "catalog_sha=(SELECT catalog_sha FROM extensions.extension_catalog_acceptances WHERE channel='stable' AND sequence=1) WHERE channel='stable';");
        else if (changed == "physical") Directory.CreateDirectory(fixture.Target);
        else fixture.Package.Catalog.Index.BlockUnknown("fixture_recovery_block");
        var count = fixture.Package.Catalog.Count("operations");
        if (changed == "settings")
            Assert.Throws<HostSettingsException>(() => fixture.Accept());
        else Assert.Throws<IOException>(() => fixture.Accept());
        Assert.Equal(count, fixture.Package.Catalog.Count("operations"));
        Assert.Equal(changed == "physical", Directory.Exists(fixture.Target));
    }

    [Fact(DisplayName = "Disposed Prepared Authority Or Root Cannot Admit An Extension Operation")]
    public void RejectsDisposedOwners()
    {
        using var fixture = new PayloadAcceptanceFixture();
        _ = fixture.Prepare(); fixture.Prepared!.Dispose();
        Assert.Throws<ObjectDisposedException>(() => fixture.Accept());
        fixture.Prepared = null;
        _ = fixture.Prepare(); fixture.Root.Dispose();
        Assert.Throws<IOException>(() => fixture.Accept());
        Assert.False(Directory.Exists(fixture.Target));
    }

    [Fact(DisplayName = "A Caller Payload Record Cannot Replace The Prepared Package Authority")]
    public void RequiresOpaqueAuthority()
    {
        using var fixture = new PayloadSourceFixture();
        var acceptance = new PayloadApplyAcceptance(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Contracts,
            fixture.Package.Catalog.Database, fixture.Roots);
        var identity = new OperationIdentity(fixture.Plan.OperationId, "local", new string('a', 64), "extension.apply");
        Assert.Equal("extension_apply_preparation_required",
            Assert.Throws<IOException>(() => acceptance.Accept(identity, fixture.Plan, DateTimeOffset.UtcNow)).Message);
        Assert.False(Directory.Exists(fixture.Plan.TargetPath));
    }

    [Fact(DisplayName = "Acceptance Sql Failure Keeps The Original Preference And Can Replay Only The Same Prepared Source")]
    public void RevertsFailedAcceptance()
    {
        using var fixture = new PayloadAcceptanceFixture();
        _ = fixture.Prepare();
        var original = new HostSettingsStore(fixture.Package.Catalog.Database).Read();
        var count = fixture.Package.Catalog.Count("operations");
        fixture.Package.Catalog.Sql("CREATE TRIGGER main.fixture_reject_extension_registration BEFORE INSERT ON operation_journals " +
            "WHEN NEW.operation_kind='extensionApply' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        try { Assert.Throws<SqliteException>(() => fixture.Accept()); }
        finally { fixture.Package.Catalog.Sql("DROP TRIGGER main.fixture_reject_extension_registration;"); }
        Assert.Equal(count, fixture.Package.Catalog.Count("operations"));
        Assert.Equal(BoundedJson.Serialize(original), BoundedJson.Serialize(new HostSettingsStore(fixture.Package.Catalog.Database).Read()));
        var actual = fixture.Accept(); Assert.True(actual.Acceptance.IsNew);
        Assert.True(fixture.Source(actual).InitializeEnabledPreference);
        Assert.Equal(BoundedJson.Serialize(original), BoundedJson.Serialize(new HostSettingsStore(fixture.Package.Catalog.Database).Read()));
        Assert.False(Directory.Exists(fixture.Target));
    }
}
