using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class DefinitionServiceTests
{
    [Fact(DisplayName = "Definition Deletion Publishes Its Actual Cleanup Isolation Before Closure Can Be Permitted")]
    public void PublishesCleanupIsolation()
    {
        using var fixture = AssetPreparationFixture.Create();
        fixture.Execute();
        fixture.Resources.Dispose();
        var service = Service(fixture);
        var catalog = service.Read();
        var changes = Remove(catalog);
        var result = service.Change(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
        var id = Assert.Single(result.CleanupJournalIds);
        Assert.True(fixture.Index.Current.IsReady);
        var isolation = Assert.Single(fixture.Index.Current.Isolations);
        Assert.Equal(id, isolation.JournalId);
        Assert.Equal("blob:" + fixture.Prepared.Plan.ReadWire().Assets[0].Content.Sha256, isolation.ResourceKey);
        var close = fixture.Lifecycle.RequestClose(Guid.NewGuid(), Guid.NewGuid());
        Assert.Null(close.Permit);
        Assert.Equal("recovery_pending", close.ReasonCode);
    }

    [Fact(DisplayName = "A New Import Of The Same Content Cannot Bypass A Published Cleanup Isolation")]
    public void RejectsIsolatedImports()
    {
        using var fixture = AssetPreparationFixture.Create();
        fixture.Execute();
        fixture.Resources.Dispose();
        var service = Service(fixture);
        var catalog = service.Read();
        var asset = fixture.Prepared.Plan.ReadWire().Assets[0];
        var input = new AutomationBundleCodec(new()).Encode(catalog, DateTimeOffset.UtcNow, catalog.Scripts[0].Id,
            [new(Guid.Parse(asset.AssetId), asset.PackagePath, asset.Content.SizeBytes, asset.Content.Sha256)]);
        var changes = Remove(catalog);
        service.Change(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
        var importer = new AutomationImportService(fixture.Instance, new(), fixture.Database, fixture.InstanceLease, fixture.ResourceManager, fixture.Index);
        var operation = Guid.NewGuid();
        var roots = catalog.Roots.ToDictionary(value => value.Id, value => value.Path);
        var preview = importer.Preview(operation, input, roots, 2);
        var error = Assert.Throws<AutomationDefinitionException>(() => importer.Apply(operation, "fixture", input, roots, 2, preview.PreviewDigest, new Dictionary<Guid, string>()));
        Assert.Equal("automation_host_unavailable", error.Message);
        Assert.Empty(service.Read().Scripts);
        Assert.Equal(2, new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals.Count);
    }

    [Fact(DisplayName = "An Expired Instance Lease Cannot Authorize A Separate Definition Service")]
    public void RequiresLiveLease()
    {
        using var fixture = AssetPreparationFixture.Create();
        var changes = AutomationDefinitionSeed.Create(Path.Combine(fixture.Root, "unaccepted"));
        fixture.InstanceLease.Dispose();
        Assert.Throws<IOException>(() => Service(fixture).Change(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow));
        Assert.Empty(new AutomationDefinitionStore(fixture.Database).Read().Scripts);
        Assert.Empty(new RecoveryJournalRegistry(fixture.Database).ReadSnapshot().Journals);
    }

    [Fact(DisplayName = "A Stale Definition Command Releases Its Lease And Does Not Invalidate An Unchanged Recovery Projection")]
    public void ReleasesStaleAdmission()
    {
        using var fixture = AssetPreparationFixture.Create();
        fixture.Execute();
        fixture.Resources.Dispose();
        var service = Service(fixture);
        var catalog = service.Read();
        var changes = new AutomationChanges(0, [], [catalog.Scripts[0] with { Name = "Changed" }], [], [], [], [], []);
        var id = Guid.NewGuid();
        Assert.Throws<AutomationConflictException>(() => service.Change(AutomationDefinitionStore.Identity(id, "fixture", changes), changes, DateTimeOffset.UtcNow));
        Assert.True(fixture.Index.Current.IsReady);
        Assert.Empty(fixture.Index.Current.Isolations);
        var current = changes with { ExpectedCatalogRevision = catalog.Revision };
        Assert.Equal(2, service.Change(AutomationDefinitionStore.Identity(id, "fixture", current), current, DateTimeOffset.UtcNow).CatalogRevision);
    }

    private static AutomationDefinitionService Service(AssetPreparationFixture fixture) =>
        new(fixture.Instance, new(), fixture.Database, fixture.InstanceLease, fixture.ResourceManager, fixture.Index);
    private static AutomationChanges Remove(AutomationCatalog catalog) => new(catalog.Revision, [], [], [], [], [], [],
        [new(AutomationDefinitionKind.Script, catalog.Scripts[0].Id, catalog.Scripts[0].Revision)]);
}
