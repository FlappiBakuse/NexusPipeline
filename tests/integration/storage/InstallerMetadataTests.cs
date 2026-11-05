using System.Runtime.Versioning;
using Microsoft.Win32;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerMetadataTests
{
    [Fact(DisplayName = "Native Installer Metadata Capture Preserves Typed Registry Bytes File Identities And Unexpanded Strings")]
    public void CapturesNativeMetadata()
    {
        using var fixture = new InstallerMetadataFixture();
        using (var key = Registry.CurrentUser.CreateSubKey(fixture.Paths.RegistryKey))
        {
            key.SetValue("String", "Current user text", RegistryValueKind.String);
            key.SetValue("Expand", "%LOCALAPPDATA%\\unchanged", RegistryValueKind.ExpandString);
            key.SetValue("Lines", new[] { "one", "two" }, RegistryValueKind.MultiString);
            key.SetValue("Binary", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
            key.SetValue("Dword", -1, RegistryValueKind.DWord);
            key.SetValue("Qword", long.MaxValue, RegistryValueKind.QWord);
        }
        var before = InstallerMetadataInventory.Capture(fixture.Application, fixture.Paths);
        var after = InstallerMetadataInventory.Capture(fixture.Application, fixture.Paths);
        Assert.True(InstallerMetadataInventory.Equal(before, after));
        Assert.True(before.Uninstall.Exists); Assert.True(before.Ownership.Exists);
        Assert.Equal(PhysicalObjectIdentity.Read(fixture.Application), before.RootIdentity);
        Assert.Equal(4, before.Files.Length);
        Assert.Equal("%LOCALAPPDATA%\\unchanged", before.Ownership.Values.Single(value => value.Name == "Expand").Text);
        Assert.Equal(new[] { "one", "two" }, before.Ownership.Values.Single(value => value.Name == "Lines").Lines);
        Assert.Equal("AQID", before.Ownership.Values.Single(value => value.Name == "Binary").Binary);
        Assert.Equal(-1L, before.Ownership.Values.Single(value => value.Name == "Dword").Number);
        Assert.Equal(long.MaxValue, before.Ownership.Values.Single(value => value.Name == "Qword").Number);
    }

    [Theory(DisplayName = "Installer Metadata Snapshots Distinguish Both Changed Bytes And Replaced Same Byte Objects")]
    [InlineData(false)]
    [InlineData(true)]
    public void DetectsChangedObjects(bool replacement)
    {
        using var fixture = new InstallerMetadataFixture();
        var before = InstallerMetadataInventory.Capture(fixture.Application, fixture.Paths);
        var bytes = File.ReadAllBytes(fixture.Engine);
        if (replacement)
        {
            File.Move(fixture.Engine, Path.Combine(fixture.Root, "retained-engine.exe"));
            File.WriteAllBytes(fixture.Engine, bytes);
        }
        else File.WriteAllBytes(fixture.Engine, [1, 3, 5]);
        var after = InstallerMetadataInventory.Capture(fixture.Application, fixture.Paths);
        Assert.False(InstallerMetadataInventory.Equal(before, after));
        Assert.Equal(replacement ? bytes : new byte[] { 1, 3, 5 }, File.ReadAllBytes(fixture.Engine));
    }

    [Theory(DisplayName = "Unknown Native Uninstaller Members Stay In Place On Metadata Admission Failure")]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesUnknownFiles(bool directory)
    {
        using var fixture = new InstallerMetadataFixture();
        var unknown = Path.Combine(fixture.Application, "uninstall", directory ? "unknown" : "unins999.exe");
        if (directory) Directory.CreateDirectory(unknown);
        var file = directory ? Path.Combine(unknown, "user.txt") : unknown;
        File.WriteAllText(file, "Unknown retained bytes");
        var identity = PhysicalObjectIdentity.Read(file);
        Assert.Throws<IOException>(() => InstallerMetadataInventory.Capture(fixture.Application, fixture.Paths));
        Assert.Equal("Unknown retained bytes", File.ReadAllText(file));
        Assert.Equal(identity, PhysicalObjectIdentity.Read(file));
    }

    [Theory(DisplayName = "Unsupported Or Excessive Installer Registry Metadata Is Rejected Without Rewriting It")]
    [InlineData("subkey")]
    [InlineData("kind")]
    [InlineData("length")]
    public void PreservesUnknownRegistry(string fault)
    {
        using var fixture = new InstallerMetadataFixture();
        using (var key = Registry.CurrentUser.CreateSubKey(fixture.Paths.RegistryKey))
        {
            if (fault == "subkey") { using var child = key.CreateSubKey("foreign"); child.SetValue("user", "retained"); }
            if (fault == "kind") key.SetValue("Unknown", new byte[] { 1, 2 }, RegistryValueKind.None);
            if (fault == "length") key.SetValue("Unknown", new string('x', 32769), RegistryValueKind.String);
        }
        Assert.Throws<IOException>(() => InstallerMetadataInventory.Capture(fixture.Application, fixture.Paths));
        using var after = Registry.CurrentUser.OpenSubKey(fixture.Paths.RegistryKey);
        Assert.NotNull(after);
        if (fault == "subkey") { using var child = after.OpenSubKey("foreign"); Assert.Equal("retained", child!.GetValue("user")); }
        if (fault == "kind") Assert.Equal(RegistryValueKind.None, after.GetValueKind("Unknown"));
        if (fault == "length") Assert.Equal(32769, ((string)after.GetValue("Unknown")!).Length);
    }

    [Fact(DisplayName = "An Installer Metadata Snapshot Includes A Native Shortcut Only When Its Target Is The Exact Public Host")]
    public void CapturesOwnedShortcut()
    {
        using var fixture = new InstallerMetadataFixture();
        fixture.WriteShortcut(Path.Combine(fixture.Application, ApplicationTree.HostEntry));
        var before = File.ReadAllBytes(fixture.Shortcut);
        var snapshot = InstallerMetadataInventory.Capture(fixture.Application, fixture.Paths);
        Assert.Single(snapshot.Files, file => file.Role == "desktop");
        Assert.Equal(before, File.ReadAllBytes(fixture.Shortcut));
    }

    [Theory(DisplayName = "A Foreign Shortcut Target Or Arguments Remain Untouched And Cannot Enter The Installer Metadata Group")]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesForeignShortcut(bool arguments)
    {
        using var fixture = new InstallerMetadataFixture();
        fixture.WriteShortcut(arguments ? Path.Combine(fixture.Application, ApplicationTree.HostEntry) : Path.Combine(fixture.Root, "foreign.exe"),
            arguments ? "--foreign" : "");
        var before = File.ReadAllBytes(fixture.Shortcut);
        Assert.Throws<IOException>(() => InstallerMetadataInventory.Capture(fixture.Application, fixture.Paths));
        Assert.Equal(before, File.ReadAllBytes(fixture.Shortcut));
    }

    [Fact(DisplayName = "Missing Installer Metadata Is Captured Read Only Without Creating Directories Keys Or Files")]
    public void LeavesMissingMetadata()
    {
        using var fixture = new InstallerMetadataFixture(seed: false);
        var snapshot = InstallerMetadataInventory.Capture(fixture.Application, fixture.Paths);
        Assert.Null(snapshot.RootIdentity); Assert.False(snapshot.Uninstall.Exists); Assert.False(snapshot.Ownership.Exists);
        Assert.Empty(snapshot.Files); Assert.False(Directory.Exists(fixture.Root));
        using var key = Registry.CurrentUser.OpenSubKey(fixture.Paths.UninstallRegistryKey);
        Assert.Null(key);
    }
}
