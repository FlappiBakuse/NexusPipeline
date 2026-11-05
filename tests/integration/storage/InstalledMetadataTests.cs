using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstalledMetadataTests
{
    [Fact(DisplayName = "Actual Applied Tree Metadata Round Trips Without Minting An Installed Revision")]
    public async Task RoundTripsMetadata()
    {
        using var fixture = new PayloadAcceptanceFixture(savedEnabled: false);
        _ = fixture.Prepare(); var accepted = fixture.Accept();
        using var reservation = await Inventory(fixture).ReserveForApplyAsync(fixture.Package.OperationId, "fixture-plugin", default);
        _ = Physical(fixture).Apply(fixture.Prepared!, reservation, default);
        var bound = Binding(fixture).Read(accepted.JournalId, accepted.Plan);
        var value = Footprint(bound);
        var codec = new InstalledFootprintCodec(fixture.Package.Catalog.Instance, fixture.Roots, fixture.Package.Catalog.Contracts);
        var reference = fixture.Package.Blobs.PersistPlan(codec.EncodeMetadata(value));
        var saved = codec.ReadMetadata(reference);
        var expected = new ExtensionInstalledMetadata("fixture-plugin", 1, true, value.Package, accepted.JournalId, reference);
        codec.RequireBinding(saved, expected, bound.Plan, bound.Plan.ExtensionSource!, bound.Physical[^1]);
        Assert.Equal(value.Payload.Snapshot.Entries.ToArray(), saved.Payload.Snapshot.Entries.ToArray());
        Assert.Equal(value.Payload.OriginalObjects.ToArray(), saved.Payload.OriginalObjects.ToArray());
        Assert.Throws<NotSupportedException>(() => ((IList<PhysicalTreeObject>)saved.Payload.OriginalObjects)[0] =
            new("", new(new string('a', 16), new string('b', 32))));
        Assert.Null(new ExtensionInstalledEvidence(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Database,
            fixture.Package.Catalog.Contracts, fixture.Roots).ReadCurrent("fixture-plugin"));
        Assert.Equal(JournalPhase.Applied, bound.Registered.Phase);
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Package.Deployed, "manifest.json")),
            File.ReadAllBytes(Path.Combine(fixture.Target, "manifest.json")));
    }

    [Theory(DisplayName = "Valid Shaped But Rebound Metadata Cannot Replace The Original Applied Source")]
    [InlineData("revision")]
    [InlineData("source")]
    [InlineData("physical")]
    public async Task RejectsReboundSources(string changed)
    {
        using var fixture = new PayloadAcceptanceFixture(savedEnabled: false);
        _ = fixture.Prepare(); var accepted = fixture.Accept();
        using var reservation = await Inventory(fixture).ReserveForApplyAsync(fixture.Package.OperationId, "fixture-plugin", default);
        _ = Physical(fixture).Apply(fixture.Prepared!, reservation, default);
        var bound = Binding(fixture).Read(accepted.JournalId, accepted.Plan); var value = Footprint(bound);
        var codec = new InstalledFootprintCodec(fixture.Package.Catalog.Instance, fixture.Roots, fixture.Package.Catalog.Contracts);
        var original = fixture.Package.Blobs.PersistPlan(codec.EncodeMetadata(value));
        var replacement = changed switch
        {
            "revision" => value with { Revision = value.Revision + 1 },
            "source" => value with { Source = original },
            _ => value with { PhysicalResult = original }
        };
        var changedReference = fixture.Package.Blobs.PersistPlan(codec.EncodeMetadata(replacement));
        var read = codec.ReadMetadata(changedReference);
        var expected = new ExtensionInstalledMetadata("fixture-plugin", 1, true, value.Package, accepted.JournalId, original);
        Assert.Throws<IOException>(() => codec.RequireBinding(read, expected, bound.Plan,
            bound.Plan.ExtensionSource!, bound.Physical[^1]));
        Assert.Equal(JournalPhase.Applied, Binding(fixture).Read(accepted.JournalId, accepted.Plan).Latest.Phase);
    }

    [Fact(DisplayName = "A Committed Sql Cursor Without Its Physical Commit Evidence Is Rejected Without Changing The Tree")]
    public async Task RequiresPhysicalCommit()
    {
        using var fixture = new PayloadAcceptanceFixture(savedEnabled: false);
        _ = fixture.Prepare(); var accepted = fixture.Accept();
        using var reservation = await Inventory(fixture).ReserveForApplyAsync(fixture.Package.OperationId, "fixture-plugin", default);
        _ = Physical(fixture).Apply(fixture.Prepared!, reservation, default);
        var bytes = File.ReadAllBytes(Path.Combine(fixture.Target, "manifest.json"));
        fixture.Package.Catalog.Sql("UPDATE operation_journals SET phase='committed' WHERE journal_id='" + accepted.JournalId + "';");
        try { Assert.Throws<IOException>(() => Binding(fixture).Read(accepted.JournalId, accepted.Plan)); }
        finally { fixture.Package.Catalog.Sql("UPDATE operation_journals SET phase='applied' WHERE journal_id='" + accepted.JournalId + "';"); }
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.Target, "manifest.json")));
        Assert.Null(new ExtensionInstalledEvidence(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Database,
            fixture.Package.Catalog.Contracts, fixture.Roots).ReadCurrent("fixture-plugin"));
    }

    [Fact(DisplayName = "Final Metadata Capacity Rejects Before Any New Workspace Or Installation Revision")]
    public void BoundsFinalMetadata()
    {
        using var fixture = new PayloadAcceptanceFixture(savedEnabled: false);
        _ = fixture.Prepare(); var accepted = fixture.Accept();
        var plan = new PayloadPlanCodec(fixture.Package.Catalog.Instance, fixture.Package.Catalog.Contracts,
            fixture.Package.Blobs, fixture.Roots).Read(accepted.Plan);
        var source = fixture.Source(accepted);
        PayloadMetadataCapacity.Require(plan, source);
        Assert.Throws<IOException>(() => PayloadMetadataCapacity.Require(plan,
            source with { ExpectedInstalledRevision = 9007199254740991 }));
        Assert.False(Directory.Exists(ExtensionWorkspacePath.For(plan)));
        Assert.False(Directory.Exists(fixture.Target));
    }

    private static ExtensionInstalledFootprint Footprint(BoundExtensionPayload bound)
    {
        var package = bound.Source.Package!;
        return new(InstalledFootprintCodec.Format, 1, bound.Plan.InstanceId, bound.Plan.OperationId,
            bound.Plan.JournalId, bound.Source.ExtensionId, 1, true,
            new(bound.Source.ExtensionId, package.Entry.PackageVersion,
                package.Entry.Kind == "managed-code" ? ExtensionPackageKind.ManagedCode : ExtensionPackageKind.DataSpecialized,
                package.Entry.ArtifactSha256, package.Entry.ManifestSha256),
            bound.Plan.TargetPath, bound.Plan.ParentIdentity, bound.Plan.ExtensionSource!,
            bound.Physical[^1].Intent.Artifact,
            bound.Plan.Candidate with { OriginalObjects = bound.Physical[^1].Record.CandidateObjects });
    }
    private static ExtensionActivator Inventory(PayloadAcceptanceFixture f) =>
        new(f.Package.Catalog.Contracts, f.Package.Catalog.Lease, f.Package.Catalog.Resources);
    private static ExtensionPhysicalJournal Physical(PayloadAcceptanceFixture f) =>
        new(f.Package.Catalog.Instance, f.Package.Catalog.Database, f.Package.Catalog.Contracts, f.Roots);
    private static ExtensionPayloadBinding Binding(PayloadAcceptanceFixture f) =>
        new(f.Package.Catalog.Instance, f.Package.Catalog.Database, f.Package.Catalog.Contracts, f.Roots);
}
