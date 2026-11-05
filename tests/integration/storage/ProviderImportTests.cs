using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ProviderImportTests
{
    [Fact]
    public async Task CapturesCurrentProvider()
    {
        await using var fixture = await ProviderRuntimeFixture.PrepareAsync("configuration", configuration: true, saveConfigurations: false);
        var source = fixture.Source;
        var overview = new ConfigurationOverviewReader(source.Instance, source.Database, source.Lease, new(), fixture.Registry);
        var before = overview.Read(source.OtherBinding);
        var resource = Assert.Single(before.Resources);
        Assert.Equal("provider-main", resource.ResourceId);
        Assert.False(resource.IsSaved);
        var original = File.ReadAllBytes(source.Configuration);
        var service = Service(fixture);
        var request = Request(before);
        var result = service.Import("provider-import", request, DateTimeOffset.UtcNow);
        var saved = Assert.Single(result.Items).Saved;
        Assert.Equal(ProviderConfigurationPolicy.Contract("provider-fixture", "main", new("provider-main", 0, 1)), saved.Contract);
        Assert.Equal(original, File.ReadAllBytes(source.Configuration));
        var after = Assert.Single(overview.Read(source.OtherBinding).Resources);
        Assert.True(after.IsSaved); Assert.Equal(1, after.Revision);
        Assert.Equal(FileObjectKind.File, after.Shape);
        Assert.Equal(1, after.FileCount);
        File.Delete(source.Configuration);
        await fixture.Scope.DisposeAsync();
        Assert.False(service.Import("provider-import", request, DateTimeOffset.UtcNow).IsNew);
        Assert.Throws<IOException>(() => overview.Read(source.OtherBinding));
    }

    [Theory]
    [InlineData("resource")]
    [InlineData("coverage")]
    [InlineData("revoked")]
    [InlineData("revision")]
    [InlineData("contract")]
    public async Task RejectsUnboundImport(string reason)
    {
        await using var fixture = await ProviderRuntimeFixture.PrepareAsync("configuration", configuration: true,
            invalidContract: reason == "contract", saveConfigurations: reason == "contract");
        var source = fixture.Source;
        var catalog = source.Definitions.Read();
        var binding = catalog.Bindings.Single(item => item.Id == source.OtherBinding);
        var script = catalog.Scripts.Single(item => item.Id == source.OtherScriptId);
        var request = new ConfigurationImportRequest(Guid.NewGuid(), binding.Id, binding.Revision, script.Revision,
            [new(reason == "resource" ? "undeclared" : "provider-main", reason == "contract" ? 1 : 0)]);
        if (reason == "revision") request = request with { ExpectedScriptRevision = script.Revision + 1 };
        if (reason == "coverage")
        {
            var change = new AutomationChanges(catalog.Revision, [], [script with { ConfigurationPaths = [] }], [], [], [], [], []);
            source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "provider-import", change), change, DateTimeOffset.UtcNow);
            request = request with { ExpectedScriptRevision = script.Revision + 1 };
        }
        if (reason == "revoked") await fixture.Scope.DisposeAsync();
        var bytes = File.ReadAllBytes(source.Configuration);
        if (reason == "revoked") Assert.Throws<IOException>(() => Service(fixture).Import("provider-import", request, DateTimeOffset.UtcNow));
        else Assert.Throws<ConfigurationException>(() => Service(fixture).Import("provider-import", request, DateTimeOffset.UtcNow));
        Assert.Equal(0, fixture.Base.Count("configuration_batch_items"));
        Assert.Equal(bytes, File.ReadAllBytes(source.Configuration));
        var reader = new ConfigurationOverviewReader(source.Instance, source.Database, source.Lease, new(), fixture.Registry);
        if (reason == "revoked") Assert.Throws<IOException>(() => reader.Read(binding.Id));
        if (reason is "coverage" or "contract") Assert.Throws<ConfigurationException>(() => reader.Read(binding.Id));
    }

    [Theory]
    [InlineData(BatchPersistencePoint.Prepared)]
    [InlineData(BatchPersistencePoint.OperationAccepted)]
    [InlineData(BatchPersistencePoint.ItemPublished)]
    public async Task RejectsRevokedCommit(BatchPersistencePoint revokeAt)
    {
        await using var fixture = await ProviderRuntimeFixture.PrepareAsync("configuration", configuration: true, saveConfigurations: false);
        var source = fixture.Source;
        var before = new ConfigurationOverviewReader(source.Instance, source.Database, source.Lease, new(), fixture.Registry).Read(source.OtherBinding);
        var observer = new RevokeObserver(revokeAt, () => fixture.Scope.DisposeAsync().AsTask().GetAwaiter().GetResult());
        Assert.Throws<IOException>(() => Service(fixture, observer).Import("provider-import", Request(before), DateTimeOffset.UtcNow));
        Assert.Null(new ConfigurationRevisionStore(source.Instance, source.Database, new()).Read(new(source.OtherBinding, "provider-main")));
        Assert.Equal(0, fixture.Base.Count("configuration_batch_items"));
    }

    private sealed class RevokeObserver(BatchPersistencePoint revokeAt, Action revoke) : IBatchPersistenceObserver
    {
        public void Reached(BatchPersistencePoint point, Guid operationId, int resourceOrdinal)
        { if (point == revokeAt) revoke(); }
    }

    private static ConfigurationImportService Service(ProviderRuntimeFixture fixture, IBatchPersistenceObserver? observer = null) =>
        new(fixture.Source.Instance, fixture.Source.Database, new(), fixture.Source.Lease, fixture.Base.Resources, observer, fixture.Registry);

    private static ConfigurationImportRequest Request(ConfigurationOverview overview) =>
        new(Guid.NewGuid(), overview.BindingId, overview.BindingRevision, overview.ScriptRevision, [new("provider-main", 0)]);
}
