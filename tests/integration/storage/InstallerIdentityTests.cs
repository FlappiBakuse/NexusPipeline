using System.Runtime.Versioning;
using Microsoft.Win32;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerIdentityTests
{
    [Fact(DisplayName = "Native DPAPI Installation Identity Binds The Current User Registry Root Runtime And Exact Worker")]
    public void BindsNativeIdentity()
    {
        using var fixture = new InstallerIdentityFixture();
        Assert.Equal(fixture.Identity, fixture.Anchor.Read(fixture.Application));
        var bytes = File.ReadAllBytes(fixture.SealPath);
        Assert.Equal(fixture.Identity, InstallationIdentityCodec.Unseal(bytes, fixture.Application));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.SealPath));
    }

    [Theory(DisplayName = "Unknown Current Installation Formats Remain Untouched After Native DPAPI Decryption")]
    [InlineData("format")]
    [InlineData("version")]
    [InlineData("generation")]
    [InlineData("user")]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("state")]
    [InlineData("product")]
    [InlineData("native")]
    [InlineData("numeric-string")]
    public void PreservesUnknownIdentity(string fault)
    {
        using var fixture = new InstallerIdentityFixture();
        var bytes = fixture.Forge(fault); fixture.Save(bytes);
        var error = Record.Exception(() => fixture.Anchor.Read(fixture.Application));
        if (fault is "extra" or "missing" or "numeric-string") Assert.IsType<System.Text.Json.JsonException>(error);
        else Assert.IsType<IOException>(error);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.SealPath));
        using var key = Registry.CurrentUser.OpenSubKey(fixture.RegistryPath);
        Assert.Equal(InstallerIdentityFixture.Digest(bytes), key!.GetValue("IdentitySha256"));
    }

    [Theory(DisplayName = "Changed Installation Seals And Registry Anchors Are Rejected Without Rewriting Them")]
    [InlineData("seal")]
    [InlineData("digest")]
    [InlineData("id")]
    [InlineData("root")]
    [InlineData("extra")]
    [InlineData("kind")]
    public void RejectsChangedAnchor(string fault)
    {
        using var fixture = new InstallerIdentityFixture();
        using (var key = Registry.CurrentUser.OpenSubKey(fixture.RegistryPath, writable: true))
        {
            switch (fault)
            {
                case "seal": File.WriteAllBytes(fixture.SealPath, [1, 2, 3]); break;
                case "digest": key!.SetValue("IdentitySha256", new string('d', 64)); break;
                case "id": key!.SetValue("InstallationId", Guid.NewGuid().ToString("D")); break;
                case "root": key!.SetValue("ApplicationRoot", fixture.Root); break;
                case "extra": key!.SetValue("Foreign", "preserved"); break;
                case "kind": key!.SetValue("InstallationId", new byte[] { 1 }, RegistryValueKind.Binary); break;
            }
        }
        var bytes = File.ReadAllBytes(fixture.SealPath);
        Assert.ThrowsAny<IOException>(() => fixture.Anchor.Read(fixture.Application));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.SealPath));
    }

    [Theory(DisplayName = "The Same Bytes At Replacement Directory Or Worker Objects Do Not Inherit Installation Ownership")]
    [InlineData("root")]
    [InlineData("runtime")]
    [InlineData("worker")]
    [InlineData("engine")]
    public void RejectsReplacedObjects(string fault)
    {
        using var fixture = new InstallerIdentityFixture();
        var bytes = File.ReadAllBytes(fixture.SealPath);
        var target = fault == "root" ? fixture.Application : fault == "runtime" ? Path.Combine(fixture.Application, "runtime") :
            fault == "engine" ? fixture.EnginePath : fixture.WorkerPath;
        var displaced = target + "-displaced";
        if (fault is "worker" or "engine")
        {
            File.Move(target, displaced); File.WriteAllBytes(target, File.ReadAllBytes(displaced));
        }
        else
        {
            Directory.Move(target, displaced); Directory.CreateDirectory(target);
            var original = fault == "root" ? Path.Combine(displaced, "runtime") : displaced;
            var runtime = fault == "root" ? Path.Combine(target, "runtime") : target;
            Directory.CreateDirectory(runtime);
            File.WriteAllBytes(Path.Combine(runtime, "instance.json"), File.ReadAllBytes(Path.Combine(original, "instance.json")));
        }
        Assert.ThrowsAny<IOException>(() => fixture.Anchor.Read(fixture.Application));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.SealPath));
        Assert.True(File.Exists(displaced) || Directory.Exists(displaced));
    }

    [Theory(DisplayName = "Partially Missing Installation Registration Or Worker Bytes Stay Available For Recovery")]
    [InlineData("registry")]
    [InlineData("seal")]
    [InlineData("worker")]
    [InlineData("engine")]
    public void PreservesIncompleteIdentity(string fault)
    {
        using var fixture = new InstallerIdentityFixture();
        if (fault == "registry") Registry.CurrentUser.DeleteSubKey(fixture.RegistryPath);
        if (fault == "seal") File.Move(fixture.SealPath, fixture.SealPath + ".retained");
        if (fault == "worker") File.WriteAllBytes(fixture.WorkerPath, [7, 8, 9]);
        if (fault == "engine") File.WriteAllBytes(fixture.EnginePath, [7, 8, 9]);
        var files = Directory.GetFiles(fixture.Manager).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        Assert.ThrowsAny<IOException>(() => fixture.Anchor.Read(fixture.Application));
        Assert.Equal(files.Keys.Order(), Directory.GetFiles(fixture.Manager).Select(Path.GetFileName).Order());
        foreach (var (name, bytes) in files) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.Manager, name!)));
        if (fault == "engine") Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(fixture.EnginePath));
    }

    [Fact(DisplayName = "An Absent Installation Anchor Does Not Adopt Or Mutate An Existing Directory")]
    public void LeavesUnownedDirectory()
    {
        using var fixture = new InstallerIdentityFixture();
        var target = Path.Combine(fixture.Root, "unowned"); Directory.CreateDirectory(target);
        var file = Path.Combine(target, "user.txt"); File.WriteAllText(file, "unowned user data");
        var missing = new InstallationAnchor(Path.Combine(fixture.Root, "missing-manager"),
            @"Software\NexusPipeline\Tests\Installer\" + Guid.NewGuid().ToString("N"));
        Assert.Null(missing.Read(target));
        Assert.Equal("unowned user data", File.ReadAllText(file));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "missing-manager")));
    }

    [Fact(DisplayName = "Completed Retained Data Identity Can Outlive Its Removed Native Uninstaller While Keeping Its Private Worker")]
    public void ReadsRetainedIdentity()
    {
        using var fixture = new InstallerIdentityFixture();
        var retained = fixture.Identity with { State = "retained-data" };
        fixture.Save(InstallationIdentityCodec.Seal(retained));
        File.Move(fixture.EnginePath, Path.Combine(fixture.Root, "retained-engine.exe"));
        Assert.Equal(retained, fixture.Anchor.Read(fixture.Application));
        Assert.True(File.Exists(fixture.WorkerPath));
    }
}
