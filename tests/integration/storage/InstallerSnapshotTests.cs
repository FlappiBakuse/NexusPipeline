using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using NexusPipeline.Platform.Storage;
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
        var expectedFiles = fixture.ReadOriginalMetadata().Select(file => new InstallerMetadataFile(file.Role, file.Name,
            file.ParentIdentity, file.Identity, file.Bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(file.Bytes)))).ToArray();
        var rootIdentity = PhysicalObjectIdentity.Read(fixture.Application);
        var manager = PhysicalObjectIdentity.Read(fixture.Paths.ManagerRoot);
        var uninstall = PhysicalObjectIdentity.Read(Path.GetDirectoryName(fixture.Engine)!);
        var expected = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "nexus.installer.snapshot", version = 1,
            snapshot = new
            {
                root = fixture.Application, rootIdentity = WireIdentity(rootIdentity),
                uninstall = new { exists = true, values = new[] { StringValue("InstallLocation", fixture.Application),
                    StringValue("UninstallString", '"' + fixture.Engine + '"') } },
                ownership = new { exists = false, values = Array.Empty<object>() },
                files = expectedFiles.Select(file => new { role = file.Role, name = file.Name,
                    parentIdentity = WireIdentity(file.ParentIdentity), identity = WireIdentity(file.Identity),
                    sizeBytes = file.SizeBytes, sha256 = file.Sha256 }),
                directories = new[] { new { role = "desktop", identity = (object?)null }, new { role = "group", identity = (object?)null },
                    new { role = "manager", identity = WireIdentity(manager) }, new { role = "uninstall", identity = WireIdentity(uninstall) } }
            }
        });
        var snapshot = InstallerMetadataInventory.Capture(fixture.Application, fixture.Paths);
        var bytes = InstallerSnapshotCodec.Encode(snapshot);
        using var expectedWire = JsonDocument.Parse(expected);
        using var encoded = JsonDocument.Parse(bytes);
        Assert.True(JsonElement.DeepEquals(expectedWire.RootElement, encoded.RootElement));
        var decoded = InstallerSnapshotCodec.Decode(expected, fixture.Application);
        Assert.Equal(fixture.Application, decoded.Root);
        Assert.Equal(rootIdentity, decoded.RootIdentity);
        Assert.Equal(expectedFiles, decoded.Files);
        Assert.Equal(new[] { new InstallerMetadataDirectory("desktop", null), new("group", null),
            new("manager", manager), new("uninstall", uninstall) }, decoded.Directories);
        Assert.True(decoded.Uninstall.Exists);
        Assert.Equal(new[] { new InstallerRegistryValue("InstallLocation", RegistryValueKind.String, fixture.Application, null, null, null),
            new("UninstallString", RegistryValueKind.String, '"' + fixture.Engine + '"', null, null, null) }, decoded.Uninstall.Values);
        Assert.False(decoded.Ownership.Exists);
        Assert.Empty(decoded.Ownership.Values);

        static object? WireIdentity(PhysicalObjectIdentity? value) => value is null ? null : new { volumeSerial = value.VolumeSerial, fileId = value.FileId };
        static object StringValue(string name, string text) => new { name, kind = 1, text,
            lines = (string[]?)null, binary = (string?)null, number = (long?)null };
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
        var text = new string('中', 15000);
        using (var key = Registry.CurrentUser.CreateSubKey(fixture.Paths.RegistryKey))
            key.SetValue("Text", text, RegistryValueKind.String);
        var snapshot = InstallerMetadataInventory.Capture(fixture.Application, fixture.Paths);
        var bytes = InstallerSnapshotCodec.Encode(snapshot);
        Assert.InRange(bytes.Length, 1, 131_072);
        using var wire = JsonDocument.Parse(bytes);
        var value = Assert.Single(wire.RootElement.GetProperty("snapshot").GetProperty("ownership").GetProperty("values").EnumerateArray());
        Assert.Equal("Text", value.GetProperty("name").GetString());
        Assert.Equal(1, value.GetProperty("kind").GetInt32());
        Assert.Equal(text, value.GetProperty("text").GetString());
        var decoded = InstallerSnapshotCodec.Decode(bytes, fixture.Application);
        Assert.True(decoded.Ownership.Exists);
        Assert.Equal(new InstallerRegistryValue("Text", RegistryValueKind.String, text, null, null, null),
            Assert.Single(decoded.Ownership.Values));
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
