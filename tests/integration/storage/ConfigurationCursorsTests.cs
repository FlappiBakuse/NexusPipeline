using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class ConfigurationCursorsTests
{
    [Fact(DisplayName = "A Real Saved Generic Configuration And Borrowed Child Are Accepted With Their Exact Frozen Manifest")]
    public void AcceptsFrozenManifests()
    {
        using var fixture = new ConfigurationCursorsFixture();
        var accepted = fixture.Accept(); Assert.True(accepted.Acceptance.IsNew);
        Assert.Equal(fixture.Attempt.Reference, accepted.Plan);
        var parent = new AttemptPlanCodec(fixture.Source.Instance, new(), new(fixture.Source.Instance, new())).Read(accepted.Plan).ReadWire();
        var childRef = Assert.Single(parent.ConfigurationPlans).Plan;
        var child = new ConfigurationPlanCodec(fixture.Source.Instance, new(), new(fixture.Source.Instance, new()))
            .Read(new(childRef.RelativePath, childRef.SizeBytes, childRef.Sha256));
        var state = new RunStateCodec(fixture.Source.Instance, new(), new(fixture.Source.Instance, new())).ReadStructure(child.RunState!);
        Assert.Equal(fixture.Saved.Address, state.Source.Address); Assert.Equal(fixture.Saved.Revision, state.Source.Revision);
        Assert.Equal(fixture.State.SnapshotReference, state.SnapshotReference);
        var journal = new RecoveryJournalRegistry(fixture.Source.Database).ReadSnapshot().Journals.Single(item => item.JournalId == accepted.JournalId);
        Assert.Contains(ConfigurationRevisionService.ResourceKey(fixture.Saved.Address), journal.ResourceKeys);
        Assert.Equal("{\"fixture\":true}", File.ReadAllText(fixture.Source.Configuration));
    }

    [Fact(DisplayName = "Saving A New Revision After Real Preparation Rejects The Old Start Without Consuming Its Operation")]
    public void RejectsAdvancedRevisions()
    {
        using var fixture = new ConfigurationCursorsFixture();
        Assert.Equal(fixture.Saved.Revision + 1, fixture.SaveNext().Saved.Revision);
        var before = fixture.Source.Snapshot();
        Assert.Equal("configuration_revision_conflict", Assert.Throws<ConfigurationException>(() => fixture.Accept()).Message);
        Assert.Null(fixture.Acceptance.LookupStart(fixture.Identity)); AssertUnchanged(fixture, before);
        Assert.Equal("{\"fixture\":true}", File.ReadAllText(fixture.Source.Configuration));
    }

    [Fact(DisplayName = "Removing The Current Pointer Does Not Turn A Frozen Saved Source Into A New Initial Configuration")]
    public void KeepsFrozenSources()
    {
        using var fixture = new ConfigurationCursorsFixture();
        fixture.Execute($"DELETE FROM configuration_current WHERE binding_id='{fixture.Saved.Address.BindingId:D}' " +
            $"AND resource_id='{fixture.Saved.Address.ResourceId}';");
        var before = fixture.Source.Snapshot();
        Assert.Equal("configuration_revision_conflict", Assert.Throws<ConfigurationException>(() => fixture.Accept()).Message);
        Assert.Null(fixture.Acceptance.LookupStart(fixture.Identity)); AssertUnchanged(fixture, before);
    }

    [Theory(DisplayName = "Current Saved Header Must Match The Prepared Source Owner Contract And Complete Manifest Reference")]
    [InlineData("user")]
    [InlineData("script")]
    [InlineData("contract-revision")]
    [InlineData("contract-digest")]
    [InlineData("instance")]
    [InlineData("requires-instance")]
    [InlineData("manifest-path")]
    [InlineData("manifest-digest")]
    [InlineData("manifest-size")]
    public void RequiresBoundHeaders(string field)
    {
        using var fixture = new ConfigurationCursorsFixture();
        var changed = new string('c', 64);
        fixture.ChangeSavedHeader(field switch
        {
            "user" => $"user_id='{fixture.Source.UserA:D}'",
            "script" => $"automation_id='{fixture.Source.OtherScriptId:D}'",
            "contract-revision" => "contract_revision=contract_revision+1",
            "contract-digest" => $"contract_digest='{changed}'",
            "instance" => "instance_key='another-instance'",
            "requires-instance" => "requires_instance=1,instance_key='required-instance'",
            "manifest-path" => $"manifest_path='blobs/plans/{changed}.json'",
            "manifest-digest" => $"manifest_digest='{changed}'",
            _ => "manifest_size=manifest_size+1"
        });
        var before = fixture.Source.Snapshot();
        Assert.Equal("configuration_run_source_changed", Assert.Throws<IOException>(() => fixture.Accept()).Message);
        Assert.Null(fixture.Acceptance.LookupStart(fixture.Identity)); AssertUnchanged(fixture, before);
    }

    [Theory(DisplayName = "The Actual Binding Header Cannot Change Behind The Frozen Catalog Cursor")]
    [InlineData(false)]
    [InlineData(true)]
    public void RechecksBindingHeaders(bool owner)
    {
        using var fixture = new ConfigurationCursorsFixture();
        var assignment = owner ? $"user_id='{fixture.Source.UserA:D}',automation_id='{fixture.Source.OtherScriptId:D}'" : "revision=revision+1";
        fixture.Execute($"UPDATE bindings SET {assignment} WHERE binding_id='{fixture.Source.BindingB:D}';");
        var before = fixture.Source.Snapshot();
        if (owner) Assert.Equal("configuration_binding_owner_changed", Assert.Throws<IOException>(() => fixture.Accept()).Message);
        else Assert.Equal("configuration_binding_revision_conflict", Assert.Throws<ConfigurationException>(() => fixture.Accept()).Message);
        Assert.Null(fixture.Acceptance.LookupStart(fixture.Identity)); AssertUnchanged(fixture, before);
    }

    [Fact(DisplayName = "A Removed Saved Asset Relationship Cannot Be Accepted As The Same Source Revision")]
    public void RechecksAssetRelations()
    {
        using var fixture = new ConfigurationCursorsFixture();
        fixture.Execute($"DELETE FROM configuration_revision_assets WHERE binding_id='{fixture.Saved.Address.BindingId:D}' " +
            $"AND resource_id='{fixture.Saved.Address.ResourceId}' AND revision={fixture.Saved.Revision};");
        var before = fixture.Source.Snapshot();
        Assert.Equal("configuration_reference_closure_invalid", Assert.Throws<IOException>(() => fixture.Accept()).Message);
        Assert.Null(fixture.Acceptance.LookupStart(fixture.Identity)); AssertUnchanged(fixture, before);
    }

    [Fact(DisplayName = "A Claim Insert Failure After Source Comparison Rolls Back Every Acceptance Row And Allows The Same Operation To Retry")]
    public void RevertsClaimFailure()
    {
        using var fixture = new ConfigurationCursorsFixture();
        fixture.Execute("CREATE TRIGGER fixture_fail_configuration_claim BEFORE INSERT ON journal_resource_claims " +
            "WHEN NEW.resource_key LIKE 'configuration:%' BEGIN SELECT RAISE(ABORT,'fixture_failure'); END;");
        var before = fixture.Source.Snapshot();
        try
        {
            Assert.Contains("fixture_failure", Assert.Throws<SqliteException>(() => fixture.Accept()).Message);
            Assert.Null(fixture.Acceptance.LookupStart(fixture.Identity)); AssertUnchanged(fixture, before);
        }
        finally { fixture.Execute("DROP TRIGGER fixture_fail_configuration_claim;"); }
        Assert.True(fixture.Accept().Acceptance.IsNew);
    }

    [Theory(DisplayName = "An Accepted Start Replays Original References After Snapshot Advancement And Missing Current Files")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplaysOriginalReferences(bool missing)
    {
        using var fixture = new ConfigurationCursorsFixture(); var accepted = fixture.Accept();
        fixture.ReleasePreparation(); Assert.Equal(fixture.Saved.Revision + 1, fixture.SaveNext().Saved.Revision);
        if (missing) { File.Delete(fixture.Source.Configuration); File.Delete(fixture.Source.Image); }
        var before = fixture.Source.Snapshot();
        var replay = Assert.IsType<AttemptAcceptanceResult>(fixture.Acceptance.LookupStart(fixture.Identity));
        Assert.False(replay.Acceptance.IsNew); Assert.Equal(accepted.Plan, replay.Plan); Assert.Equal(accepted.JournalId, replay.JournalId);
        Assert.Equal(replay, fixture.Accept()); AssertUnchanged(fixture, before);
    }

    [Theory(DisplayName = "Public Raw Acceptance And Candidate Lookup Cannot Bypass The Opaque Start Boundary")]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiresOpaqueStart(bool accepted)
    {
        using var fixture = new ConfigurationCursorsFixture();
        var original = accepted ? fixture.Accept() : null;
        var candidate = fixture.Attempt.Plan.ReadWire(); var before = fixture.Source.Snapshot();
        Assert.Equal("execution_start_requires_opaque_draft", Assert.Throws<IOException>(() =>
            fixture.Acceptance.Accept(fixture.Identity, candidate, DateTimeOffset.UtcNow)).Message);
        Assert.Equal("execution_start_requires_opaque_draft", Assert.Throws<IOException>(() =>
            fixture.Acceptance.Lookup(fixture.Identity, candidate)).Message);
        AssertUnchanged(fixture, before);
        if (accepted) Assert.Equal(original!.Plan, fixture.Acceptance.LookupStart(fixture.Identity)!.Plan);
        else Assert.Null(fixture.Acceptance.LookupStart(fixture.Identity));
        Assert.Equal(!accepted, fixture.Accept().Acceptance.IsNew);
    }

    private static void AssertUnchanged(ConfigurationCursorsFixture fixture, (string[] Rows, string[] Files) before)
    {
        var after = fixture.Source.Snapshot(); Assert.Equal(before.Rows, after.Rows); Assert.Equal(before.Files, after.Files);
    }
}
