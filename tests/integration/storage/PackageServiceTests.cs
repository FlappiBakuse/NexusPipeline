using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PackageServiceTests
{
    [Fact(DisplayName = "Cached Signed Package Details Read Bounded Verified Documents Without Installing")]
    public async Task ReadsCatalogDetails()
    {
        await using var fixture = new PackageServiceFixture(payloadEdit: path =>
        {
            File.WriteAllText(Path.Combine(path, "README.md"), "Verified readme");
            File.WriteAllText(Path.Combine(path, "CHANGELOG.md"), "Verified changes");
        });
        var catalog = await fixture.Service.RefreshAsync("local", ExtensionStoreChannel.Stable, default);
        var missing = await fixture.Service.CatalogDetailsAsync(catalog.Identity with { Sequence = catalog.Identity.Sequence + 1 },
            "fixture-plugin", default);
        Assert.Null(missing); Assert.Equal(3, fixture.Requests);
        var details = await fixture.Service.CatalogDetailsAsync(catalog.Identity, "fixture-plugin", default);
        Assert.NotNull(details); Assert.Equal(catalog.Identity, details.Identity);
        Assert.Equal("Verified readme", details.Documents["README.md"]);
        Assert.Equal("Verified changes", details.Documents["CHANGELOG.md"]);
        Assert.Equal("fixture-plugin", details.Manifest.GetProperty("extensionId").GetString());
        Assert.Empty(fixture.Runtime.ReadStates()); Assert.Equal(4, fixture.Requests);
    }
    [Theory(DisplayName = "The Core Facing Package Use Case Fetches Verifies Accepts And Uses The One Actual Host Inventory")]
    [InlineData(false)] [InlineData(true)]
    public async Task UsesHostInventory(bool enabled)
    {
        await using var fixture = new PackageServiceFixture(enabled);
        await fixture.Runtime.StartAsync(default); IExtensionPackages packages = fixture.Service;
        Assert.True(packages.HasReleaseTrust); Assert.Null(packages.ReadCached(ExtensionStoreChannel.Stable));
        var catalog = await packages.RefreshAsync("local", ExtensionStoreChannel.Stable, default);
        Assert.Equal(catalog.Identity, packages.ReadCached(ExtensionStoreChannel.Stable)!.Identity);
        var id = Guid.NewGuid(); var command = fixture.Command(catalog);
        var accepted = await packages.ApplyAsync(id, "local", command, default);
        Assert.Equal(id, accepted.OperationId); Assert.NotEqual(Guid.Empty, accepted.JournalId);
        await fixture.WaitCompletedAsync(id);
        IOperationQueries queries = fixture.Queries; var operation = queries.Read(id, "local");
        Assert.NotNull(operation); Assert.Equal("extension.apply", operation.Kind); Assert.Equal("completed", operation.Status);
        Assert.Equal(accepted.JournalId, operation.InitialJournalId); Assert.Null(queries.Read(id, "another-subject"));
        Assert.Null(queries.Read(Guid.NewGuid(), "local"));
        var bound = new ExtensionPayloadBinding(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Database,
            fixture.Package.Catalog.Contracts, fixture.Roots).Read(accepted.JournalId);
        Assert.Equal(JournalPhase.Committed, bound.Registered.Phase); Assert.NotNull(bound.Commit);
        var state = Assert.Single(fixture.Runtime.ReadStates()); Assert.True(state.IsInstalled);
        Assert.Equal(enabled, state.DesiredEnabled); Assert.Equal(enabled, state.ActivationId.HasValue);
        var replay = await packages.ApplyAsync(id, "local", command, default);
        Assert.Equal(accepted.JournalId, replay.JournalId); Assert.Equal("completed", replay.Status);
        await Assert.ThrowsAsync<OperationConflictException>(() => packages.ApplyAsync(id, "another-subject", command, default));
        Assert.Equal(4, fixture.Requests);
    }

    [Fact(DisplayName = "Public Catalog Display Metadata Cannot Replace The Original Signed Selected Entry")]
    public async Task RequiresSignedEntry()
    {
        await using var fixture = new PackageServiceFixture();
        var catalog = await fixture.Service.RefreshAsync("local", ExtensionStoreChannel.Stable, default);
        var command = fixture.Command(catalog); var id = Guid.NewGuid();
        var changed = command with { Candidate = command.Candidate! with { ArtifactSha256 = new string('e', 64) } };
        var failure = await Assert.ThrowsAsync<IOException>(() => fixture.Service.ApplyAsync(id, "local", changed, default));
        Assert.Equal("extension_apply_selection_changed", failure.Message);
        Assert.Null(fixture.Queries.Read(id, "local")); Assert.Equal(3, fixture.Requests);
        Assert.Empty(fixture.Runtime.ReadStates());
    }

    [Fact(DisplayName = "A Host Without Provisioned Release Trust Cannot Fetch Or Create An Installation")]
    public async Task RequiresReleaseTrust()
    {
        await using var fixture = new PackageServiceFixture(trusted: false);
        Assert.False(fixture.Service.HasReleaseTrust);
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.Service.RefreshAsync("local", ExtensionStoreChannel.Stable, default));
        Assert.Equal("extension_release_trust_unprovisioned", error.Message);
        var id = Guid.NewGuid(); var verified = fixture.Package.Catalog.Verify(fixture.Package.Signed());
        var command = fixture.Command(new(verified.Identity, verified.PublishedAt, verified.Entries));
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.ApplyAsync(id, "local", command, default));
        Assert.Null(fixture.Queries.Read(id, "local")); Assert.Equal(0, fixture.Requests); Assert.Empty(fixture.Runtime.ReadStates());
    }
}
