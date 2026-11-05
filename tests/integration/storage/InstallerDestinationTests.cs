using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerDestinationTests
{
    [Theory(DisplayName = "A New Installation Requires An Empty Or Missing Local Directory And Leaves It Unchanged")]
    [InlineData(false)]
    [InlineData(true)]
    public void InspectsNewDirectory(bool existing)
    {
        var root = NewRoot(); var application = Path.Combine(root, "new");
        Directory.CreateDirectory(root);
        if (existing) Directory.CreateDirectory(application);
        var identity = Directory.Exists(application) ? PhysicalObjectIdentity.Read(application) : null;
        var result = InstallationDestination.Inspect(application, Paths(root), []);
        Assert.Equal(InstallationDestinationKind.New, result.Kind); Assert.Null(result.Identity);
        Assert.Equal(existing, Directory.Exists(application));
        if (existing) Assert.Equal(identity, PhysicalObjectIdentity.Read(application));
    }

    [Theory(DisplayName = "New Installation Rejects Unknown Files And Nonempty Directories Without Taking Ownership")]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesUnknownDirectory(bool file)
    {
        var root = NewRoot(); Directory.CreateDirectory(root);
        var application = Path.Combine(root, "unknown");
        var marker = file ? application : Path.Combine(application, "user.txt");
        if (!file) Directory.CreateDirectory(application);
        File.WriteAllText(marker, "Untouched user bytes");
        var identity = PhysicalObjectIdentity.Read(marker);
        Assert.Throws<IOException>(() => InstallationDestination.Inspect(application, Paths(root), []));
        Assert.Equal("Untouched user bytes", File.ReadAllText(marker));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(marker));
        Assert.False(Directory.Exists(Path.Combine(root, "manager")));
    }

    [Theory(DisplayName = "Current Installation Cannot Overlap Old Generation Roots Or Its External Metadata Manager")]
    [InlineData("equal")]
    [InlineData("inside")]
    [InlineData("parent")]
    [InlineData("manager")]
    public void ProtectsGenerationRoots(string relation)
    {
        var root = NewRoot(); Directory.CreateDirectory(root);
        var reserved = Path.Combine(root, "protected"); Directory.CreateDirectory(reserved);
        var target = relation switch
        {
            "inside" => Path.Combine(reserved, "new"), "parent" => root,
            "manager" => Path.Combine(root, "manager", "new"), _ => reserved
        };
        Assert.Throws<IOException>(() => InstallationDestination.Inspect(target, Paths(root), [reserved]));
        Assert.Empty(Directory.EnumerateFileSystemEntries(reserved));
    }

    [Theory(DisplayName = "Owned And Retained Current Directories Keep Their Sealed Instance And Require Their Respective Installation Route")]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectsOwnedRoute(bool retained)
    {
        using var fixture = new InstallerIdentityFixture();
        var identity = fixture.Identity with { State = retained ? "retained-data" : "active" };
        fixture.Save(InstallationIdentityCodec.Seal(identity));
        var result = InstallationDestination.Inspect(fixture.Application, new(fixture.Manager, fixture.RegistryPath), []);
        Assert.Equal(retained ? InstallationDestinationKind.Retained : InstallationDestinationKind.Owned, result.Kind);
        Assert.Equal(identity, result.Identity);
    }

    [Fact(DisplayName = "Retained Data Registration Does Not Authorize Replacing An Unexpected Application Payload")]
    public void PreservesRetainedPayload()
    {
        using var fixture = new InstallerIdentityFixture();
        fixture.Save(InstallationIdentityCodec.Seal(fixture.Identity with { State = "retained-data" }));
        var path = Path.Combine(fixture.Application, ApplicationTree.HostEntry);
        File.WriteAllText(path, "Unknown replacement");
        Assert.Throws<IOException>(() => InstallationDestination.Inspect(fixture.Application, new(fixture.Manager, fixture.RegistryPath), []));
        Assert.Equal("Unknown replacement", File.ReadAllText(path));
    }

    [Fact(DisplayName = "Test Installers Require An Explicit Native Fixture And Resolve Only Its Isolated Manager And Registry")]
    public void RequiresTestScope()
    {
        var root = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!;
        var marker = Path.Combine(root, ".nexus-installer-fixture");
        Assert.False(File.Exists(marker));
        Assert.ThrowsAny<IOException>(() => InstallerPaths.Current());
        var id = Guid.NewGuid().ToString("N");
        using (var output = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            output.Write(System.Text.Encoding.ASCII.GetBytes(id));
        try
        {
            Assert.Equal(new(Path.Combine(root, "installer-manager"), @"Software\NexusPipeline\Tests\Installer\" + id), InstallerPaths.Current());
            File.WriteAllText(marker, "unknown fixture");
            Assert.Throws<IOException>(() => InstallerPaths.Current());
        }
        finally { File.Delete(marker); }
    }

    private static string NewRoot() => Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "installer-destination-" + Guid.NewGuid().ToString("N"));
    private static InstallerPaths Paths(string root) => new(Path.Combine(root, "manager"), @"Software\NexusPipeline\Tests\Installer\" + Guid.NewGuid().ToString("N"));
}
