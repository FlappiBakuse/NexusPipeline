using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerPayloadTests
{
    [Fact(DisplayName = "Installation Admission Holds The Actual Published Test Application And Its Embedded SDK Identity")]
    public void BindsPublishedPayload()
    {
        var application = PublishedRoot();
        var bytes = File.ReadAllBytes(Path.Combine(application, ApplicationTree.ManifestPath));
        using var payload = InstallationPayload.Open(application, ApplicationPayloadSeed.Digest(bytes), "test");
        Assert.Equal(application, payload.Root);
        Assert.Equal(PhysicalObjectIdentity.Read(application), payload.RootIdentity);
        Assert.Equal("0.18.0", payload.Manifest.HostVersion);
        Assert.Equal(ApplicationPayloadSeed.Digest(bytes), payload.ManifestSha256);
        using var borrowed = payload.Borrow();
    }

    [Fact(DisplayName = "Installation Inspection Rejects A Manifest Outside The Frozen Setup Input")]
    public void RequiresFrozenManifest()
    {
        var application = PublishedRoot();
        Assert.Throws<IOException>(() => InstallationPayload.Open(application, new('a', 64), "test"));
    }

    [Fact(DisplayName = "Production Installation Admission Rejects Actual Test Profile Single File Programs")]
    public void RejectsTestPrograms()
    {
        var application = ControlledPayload();
        var bytes = File.ReadAllBytes(Path.Combine(application, ApplicationTree.ManifestPath));
        Assert.Throws<IOException>(() => InstallationPayload.Open(application, ApplicationPayloadSeed.Digest(bytes), "production"));
    }

    [Fact(DisplayName = "The Shared Application Lease Denies Payload Writes And Moves While Keeping Runtime And Root Neighbors Available")]
    public void ProtectsPayloadLease()
    {
        var fixture = ControlledPayload();
        var bytes = File.ReadAllBytes(Path.Combine(fixture, ApplicationTree.ManifestPath));
        var readme = Path.Combine(fixture, "README.md"); var original = File.ReadAllBytes(readme);
        using (var payload = InstallationPayload.Open(fixture, ApplicationPayloadSeed.Digest(bytes), "test"))
        {
            Assert.Throws<IOException>(() => File.WriteAllBytes(readme, [1]));
            Assert.Throws<IOException>(() => Directory.Move(Path.Combine(fixture, "resources"), Path.Combine(fixture, "replaced")));
            File.WriteAllText(Path.Combine(fixture, "notes.txt"), "Unrelated user content");
            using var borrowed = payload.Borrow();
            Assert.Equal(original, File.ReadAllBytes(readme));
        }
        File.WriteAllBytes(readme, original);
        Assert.Equal("Unrelated user content", File.ReadAllText(Path.Combine(fixture, "notes.txt")));
    }

    [Theory(DisplayName = "Unknown Or Changed Controlled Installation Payload Bytes Are Preserved On Admission Failure")]
    [InlineData("extra")]
    [InlineData("changed")]
    [InlineData("missing")]
    public void PreservesChangedPayload(string fault)
    {
        var fixture = ControlledPayload();
        var bytes = File.ReadAllBytes(Path.Combine(fixture, ApplicationTree.ManifestPath));
        var path = Path.Combine(fixture, fault == "extra" ? "resources/foreign.txt" : "README.md");
        if (fault == "missing") File.Move(path, Path.Combine(fixture, "retained-readme.txt"));
        else File.WriteAllText(path, "Retain these exact changed bytes");
        var observed = File.Exists(path) ? File.ReadAllBytes(path) : null;
        Assert.Throws<IOException>(() => InstallationPayload.Open(fixture, ApplicationPayloadSeed.Digest(bytes), "test"));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture, ApplicationTree.ManifestPath)));
        if (observed is null) Assert.False(File.Exists(path));
        else Assert.Equal(observed, File.ReadAllBytes(path));
    }

    private static string ControlledPayload()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "installer-payload-" + Guid.NewGuid().ToString("N"));
        ApplicationPayloadSeed.Prepare(root);
        var source = PublishedRoot();
        foreach (var entry in new[] { ApplicationTree.HostEntry, ApplicationTree.MaintenanceEntry })
            File.Copy(Path.Combine(source, entry), Path.Combine(root, entry), overwrite: true);
        ApplicationPayloadSeed.Write(root);
        return root;
    }

    private static string PublishedRoot()
    {
        var receipt = Environment.GetEnvironmentVariable("NEXUS_PRODUCT_ASSEMBLY") ?? throw new IOException("product_assembly_required");
        using var document = JsonDocument.Parse(File.ReadAllBytes(receipt));
        Assert.Equal("PASS_APPLICATION_ASSEMBLED", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("test", document.RootElement.GetProperty("profile").GetString());
        return Path.GetDirectoryName(receipt)!;
    }
}
