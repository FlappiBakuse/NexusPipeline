using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CommittedRecognitionTests
{
    [Theory(DisplayName = "A Real Committed Installation Is Recognized By The Existing Startup Owner Without Replaying Activation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecognizesCommittedInstall(bool enabled)
    {
        using var fixture = new InstallRuntimeFixture(savedEnabled: enabled);
        var result = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        var before = new JournalStore(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Contracts)
            .ReadAllRevisions(fixture.Accepted.JournalId).Select(item => item.Export()).ToArray();
        var target = NexusPipeline.Platform.Storage.PhysicalObjectIdentity.Read(fixture.Target);
        var index = new StartupJournalRecovery(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Contracts,
            fixture.Package.Catalog.Database, fixture.Roots).Recover(fixture.Package.Catalog.Lease);
        Assert.True(index.Current.IsReady);
        Assert.Equal(target, NexusPipeline.Platform.Storage.PhysicalObjectIdentity.Read(fixture.Target));
        var after = new JournalStore(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Contracts)
            .ReadAllRevisions(fixture.Accepted.JournalId).Select(item => item.Export()).ToArray();
        Assert.Equal(before.Length, after.Length);
        for (var i = 0; i < before.Length; i++) Assert.Equal(before[i], after[i]);
        Assert.Equal(enabled, result.IsEnabled);
        if (result.ActivationId is { } activation)
        {
            Assert.True(fixture.Activator.IsPublished("fixture-plugin", activation));
            using var closed = await fixture.Activator.ReserveForApplyAsync(fixture.Package.OperationId, "fixture-plugin", default);
        }
    }
}
