using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Maintenance;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class MaintenanceContentTests
{
    [Fact]
    public void PinsVerifiedContents()
    {
        using var fixture = new MaintenanceBinaryFixture();
        var contracts = new NexusPipeline.Platform.Serialization.ContractReader();
        var blobs = new ImmutableBlobStore(fixture.Instance, contracts);
        var roots = new PayloadApplyRoots(fixture.Plan.TargetPath, Path.Combine(fixture.Instance.RuntimeRoot, "extensions"));
        BlobReference reference;
        using (var prepared = new PayloadPlanCodec(fixture.Instance, contracts, blobs, roots).Freeze(fixture.Plan))
            reference = prepared.Reference;
        var source = new UpdateSourceCodec(fixture.Instance, contracts).ReadMetadata(fixture.Plan.MaintenanceSource!);
        var bound = new BoundMaintenancePayload(fixture.Plan, reference, source, null!, null!, [], [], []);
        var verifier = UpdateSourceSeed.Verifier(contracts, "test");
        var archive = Path.Combine(fixture.Instance.RuntimeRoot, source.Archive.RelativePath);
        var directory = Path.GetDirectoryName(archive)!;
        using (var held = MaintenanceRestorationInputs.Hold(fixture.Instance, fixture.Database, contracts, verifier, bound))
        {
            held.Require(fixture.Instance, reference, verifier);
            Assert.Throws<IOException>(() => { using var write = File.Open(archive, FileMode.Open, FileAccess.Write); });
            Assert.Throws<IOException>(() => Directory.Move(directory, directory + "-moved"));
            Assert.Throws<IOException>(() => held.Require(fixture.Instance with { InstanceId = Guid.NewGuid() }, reference, verifier));
            Assert.Throws<IOException>(() => held.Require(fixture.Instance, reference with { Sha256 = new('0', 64) }, verifier));
            Assert.Throws<IOException>(() => held.Require(fixture.Instance, reference, UpdateSourceSeed.Verifier(contracts, "test")));
            held.Dispose();
            Assert.Throws<IOException>(() => held.Require(fixture.Instance, reference, verifier));
        }
        using (var write = File.Open(archive, FileMode.Open, FileAccess.Write)) Assert.True(write.CanWrite);
        blobs.Verify(source.Archive);
    }
}
