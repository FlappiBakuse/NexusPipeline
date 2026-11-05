using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PayloadRequestTests
{
    [Fact(DisplayName = "The Original Durable Request Replays Without Minting Another Plan After Preparation Is Disposed")]
    public void ReplaysDisposedPreparation()
    {
        using var fixture = new PayloadAcceptanceFixture(savedEnabled: false);
        _ = fixture.Prepare(); var original = fixture.Accept(); var source = fixture.Source(original);
        var request = ExtensionApplyRequest.FromSource(source);
        fixture.Prepared!.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose();
        var settings = new HostSettingsStore(fixture.Package.Catalog.Database); var before = settings.Read();
        var changed = new HostSettingsChange(before.Revision, before.Values with { Extensions = [new("fixture-plugin", true)] });
        _ = settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "local", changed), changed, DateTimeOffset.UtcNow);
        var paths = Directory.GetFiles(Path.Combine(fixture.Package.Catalog.Instance.RuntimeRoot, "blobs", "plans"));
        var operations = fixture.Package.Catalog.Count("operations");
        var replay = Lookup(fixture).ReadAccepted(fixture.Package.OperationId, "local", request);
        Assert.NotNull(replay); Assert.False(replay.Acceptance.IsNew);
        Assert.Equal(original.JournalId, replay.JournalId); Assert.Equal(original.Plan, replay.Plan);
        Assert.Equal(operations, fixture.Package.Catalog.Count("operations"));
        Assert.Equal(paths.Order(StringComparer.Ordinal), Directory.GetFiles(
            Path.Combine(fixture.Package.Catalog.Instance.RuntimeRoot, "blobs", "plans")).Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(fixture.Target));
    }

    [Theory(DisplayName = "Reused Operation Identity Still Rejects A Changed Original Request")]
    [InlineData("subject")]
    [InlineData("settings")]
    [InlineData("catalog")]
    public void RejectsChangedRequests(string changed)
    {
        using var fixture = new PayloadAcceptanceFixture(savedEnabled: false);
        _ = fixture.Prepare(); var original = fixture.Accept();
        var request = ExtensionApplyRequest.FromSource(fixture.Source(original));
        if (changed == "settings") request = request with { ExpectedSettingsRevision = request.ExpectedSettingsRevision + 1 };
        if (changed == "catalog") request = request with { Catalog = request.Catalog! with { Sha256 = new string('a', 64) } };
        Assert.Throws<OperationConflictException>(() => Lookup(fixture).ReadAccepted(
            fixture.Package.OperationId, changed == "subject" ? "another-local-subject" : "local", request));
        Assert.False(Directory.Exists(fixture.Target));
        Assert.Equal(original.Plan, fixture.Accept().Plan);
    }

    [Fact(DisplayName = "Workspace Mutation Is Frozen And Cannot Be Silently Removed From The Source")]
    public void FreezesWorkspaceMutation()
    {
        using var fixture = new PayloadAcceptanceFixture(savedEnabled: false);
        _ = fixture.Prepare(); var original = fixture.Accept(); var source = fixture.Source(original);
        var parent = PhysicalObjectIdentity.Read(Path.GetDirectoryName(fixture.Target)!);
        var workspace = ExtensionWorkspacePath.For(fixture.Target, fixture.Package.OperationId);
        var key = PhysicalResourceAdmission.EntryKey(parent, Path.GetFileName(workspace));
        Assert.Equal(ResourceClaimMode.Mutation, Assert.Single(source.Claims, claim => claim.ResourceKey == key).Mode);
        Assert.Null(fixture.Package.Catalog.Resources.TryAcquire(Guid.NewGuid(),
            PhysicalResourceAdmission.Read(workspace, ResourceClaimMode.Reference)).Lease);
        var codec = new NexusPipeline.Platform.Storage.Journals.PayloadPlanCodec(
            fixture.Package.Catalog.Instance, fixture.Package.Catalog.Contracts, fixture.Package.Blobs, fixture.Roots);
        var plan = codec.Read(original.Plan);
        var altered = source with { Claims = source.Claims.Where(claim => claim.ResourceKey != key).ToImmutableArray() };
        Assert.Throws<IOException>(() => new PayloadSourceCodec(
            fixture.Package.Catalog.Instance, fixture.Package.Catalog.Contracts).RequireFrozenBinding(altered, plan));
        Assert.False(Directory.Exists(workspace));
    }

    private static PayloadAcceptanceLookup Lookup(PayloadAcceptanceFixture fixture) => new(
        fixture.Package.Catalog.Instance, fixture.Package.Catalog.Database, fixture.Package.Catalog.Contracts, fixture.Roots);
}
