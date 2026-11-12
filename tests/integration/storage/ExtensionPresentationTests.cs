using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ExtensionPresentationTests
{
    [Theory(DisplayName = "Presentation Reads Keep Returned Byte Digests And Reject Changed Installation Inventory")]
    [InlineData("manifest")] [InlineData("namespace")] [InlineData("identity")]
    public async Task RejectsChangedPresentation(string change)
    {
        using var fixture = new InstallRuntimeFixture(savedEnabled: false);
        _ = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose();
        var owner = fixture.Package.Catalog;
        var reader = new ExtensionDetailsReader(owner.Instance, owner.Database, owner.Lease, owner.Contracts, fixture.Roots);
        Assert.Equal("0.5.0", reader.Read("fixture-plugin")!.PackageVersion);
        var path = Path.Combine(fixture.Target, "manifest.json");
        if (change == "namespace") File.WriteAllBytes(Path.Combine(fixture.Target, "unexpected.bin"), [17]);
        else if (change == "identity")
        {
            var bytes = File.ReadAllBytes(path);
            File.Move(path, Path.Combine(fixture.Package.Root, "original-manifest.json")); File.WriteAllBytes(path, bytes);
        }
        else
        {
            var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
        }
        Assert.Throws<IOException>(() => reader.Read("fixture-plugin"));
    }
}
