using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerSnapshotTests
{
    [Fact(DisplayName = "The Current Installer Snapshot Roundtrips Typed Native Metadata And Explicit Missing Directory Facts")]
    public void RoundtripsNativeSnapshot()
    {
        using var fixture = new InstallerMetadataFixture();
        var snapshot = InstallerMetadataInventory.Capture(fixture.Application, fixture.Paths);
        var bytes = InstallerSnapshotCodec.Encode(snapshot);
        var decoded = InstallerSnapshotCodec.Decode(bytes, fixture.Application);
        Assert.True(InstallerMetadataInventory.Equal(snapshot, decoded));
        Assert.Equal(new[] { "desktop", "group", "manager", "uninstall" }, decoded.Directories.Select(directory => directory.Role));
        Assert.Null(decoded.Directories.Single(directory => directory.Role == "desktop").Identity);
        Assert.NotNull(decoded.Directories.Single(directory => directory.Role == "manager").Identity);
    }

    [Theory(DisplayName = "Unknown Or Inconsistent Installer Snapshot Wire Data Is Rejected Without Rewriting Native Metadata")]
    [InlineData("format")]
    [InlineData("version")]
    [InlineData("numeric-string")]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("directory")]
    [InlineData("path")]
    [InlineData("parent")]
    [InlineData("hash")]
    [InlineData("registry")]
    public void PreservesUnknownSnapshot(string fault)
    {
        using var fixture = new InstallerMetadataFixture();
        var before = InstallerMetadataInventory.Capture(fixture.Application, fixture.Paths);
        var value = JsonNode.Parse(InstallerSnapshotCodec.Encode(before))!;
        switch (fault)
        {
            case "format": value["format"] = "unknown.snapshot"; break;
            case "version": value["version"] = 2; break;
            case "numeric-string": value["version"] = "1"; break;
            case "extra": value["foreign"] = true; break;
            case "missing": value["snapshot"]!.AsObject().Remove("directories"); break;
            case "directory": value["snapshot"]!["directories"]![0]!["role"] = "manager"; break;
            case "path": value["snapshot"]!["files"]![0]!["name"] = "../foreign.txt"; break;
            case "parent": value["snapshot"]!["files"]![0]!["parentIdentity"]!["fileId"] = new string('a', 32); break;
            case "hash": value["snapshot"]!["files"]![0]!["sha256"] = "unverified"; break;
            case "registry": value["snapshot"]!["uninstall"]!["values"]![0]!["number"] = 1; break;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value); var original = bytes.ToArray();
        var error = Record.Exception(() => InstallerSnapshotCodec.Decode(bytes, fixture.Application));
        if (fault is "extra" or "missing" or "numeric-string") Assert.IsType<JsonException>(error);
        else Assert.IsType<IOException>(error);
        Assert.Equal(original, bytes);
        Assert.True(InstallerMetadataInventory.Equal(before, InstallerMetadataInventory.Capture(fixture.Application, fixture.Paths)));
    }

    [Fact(DisplayName = "Installer Snapshot Root Binding Refuses Reuse For Another Installation")]
    public void BindsSnapshotRoot()
    {
        using var fixture = new InstallerMetadataFixture();
        var bytes = InstallerSnapshotCodec.Encode(InstallerMetadataInventory.Capture(fixture.Application, fixture.Paths));
        Assert.Throws<IOException>(() => InstallerSnapshotCodec.Decode(bytes, Path.Combine(fixture.Root, "other")));
    }

    [Fact(DisplayName = "Accepted Unicode Installer Registry Text Remains Within The Same Encode Decode Byte Budget")]
    public void RoundtripsRegistryText()
    {
        using var fixture = new InstallerMetadataFixture();
        using (var key = Registry.CurrentUser.CreateSubKey(fixture.Paths.RegistryKey))
            key.SetValue("Text", new string('中', 15000), RegistryValueKind.String);
        var snapshot = InstallerMetadataInventory.Capture(fixture.Application, fixture.Paths);
        Assert.True(InstallerMetadataInventory.Equal(snapshot,
            InstallerSnapshotCodec.Decode(InstallerSnapshotCodec.Encode(snapshot), fixture.Application)));
    }

    [Theory(DisplayName = "Empty Installer Metadata Directories Retain Their Existence And Native Identity Across Compensation Planning")]
    [InlineData(false)]
    [InlineData(true)]
    public void BindsEmptyDirectories(bool existing)
    {
        using var fixture = new InstallerMetadataFixture(seed: false);
        Directory.CreateDirectory(fixture.Application);
        if (existing) Directory.CreateDirectory(fixture.Paths.DesktopRoot);
        var before = InstallerMetadataInventory.Capture(fixture.Application, fixture.Paths);
        if (existing) Directory.Move(fixture.Paths.DesktopRoot, Path.Combine(fixture.Root, "retained-desktop"));
        Directory.CreateDirectory(fixture.Paths.DesktopRoot);
        var after = InstallerMetadataInventory.Capture(fixture.Application, fixture.Paths);
        Assert.Empty(before.Files); Assert.Empty(after.Files);
        Assert.False(InstallerMetadataInventory.Equal(before, after));
    }
}
