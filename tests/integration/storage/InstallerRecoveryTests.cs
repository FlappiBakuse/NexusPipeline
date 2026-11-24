using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerRecoveryTests
{
    [Theory(DisplayName = "Current Setup Compensation Restores Original Metadata Objects Or The Exact Fresh State And Supports Repeated Requests")]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoresNativeMetadata(bool fresh)
    {
        using var fixture = new InstallerRecoveryFixture(fresh);
        var paths = MetadataPaths(fixture.Metadata);
        var originals = RuntimeFingerprint.CaptureFileEvidence(paths);
        RequireOriginalFiles(fixture.Metadata, fresh);
        RequireOriginalRegistry(fixture.Metadata, fresh);
        fixture.InstallMetadata();
        for (var pass = 0; pass < 2; pass++)
        {
            fixture.Recovery.RestoreCurrentMetadata(fixture.Metadata.Application, fixture.Transaction);
            Assert.Equal(originals, RuntimeFingerprint.CaptureFileEvidence(paths));
            RequireOriginalRegistry(fixture.Metadata, fresh);
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Backup.DirectoryPath));
        }
    }

    [Fact(DisplayName = "Installer Compensation Restores Native Registry Types And Literal Values From The Frozen Original Snapshot")]
    public void RestoresTypedRegistry()
    {
        using var fixture = new InstallerRecoveryFixture(typedValues: true);
        fixture.InstallMetadata(typedValues: true);
        fixture.Recovery.RestoreCurrentMetadata(fixture.Metadata.Application, fixture.Transaction);
        using var ownership = Registry.CurrentUser.OpenSubKey(fixture.Metadata.Paths.RegistryKey);
        Assert.NotNull(ownership);
        Assert.Equal(new[] { "Binary", "Dword", "Expand", "Lines", "Qword", "String" }, ownership.GetValueNames().Order(StringComparer.Ordinal));
        RequireRegistryValue(ownership, "String", RegistryValueKind.String, "Original text");
        RequireRegistryValue(ownership, "Expand", RegistryValueKind.ExpandString, "%LOCALAPPDATA%\\original");
        RequireRegistryValue(ownership, "Lines", RegistryValueKind.MultiString, new[] { "one", "two" });
        RequireRegistryValue(ownership, "Binary", RegistryValueKind.Binary, new byte[] { 1, 2 });
        RequireRegistryValue(ownership, "Dword", RegistryValueKind.DWord, -1);
        RequireRegistryValue(ownership, "Qword", RegistryValueKind.QWord, long.MaxValue);
    }

    [Theory(DisplayName = "Partial Installer Metadata Compensation Resumes With Original File Identities And Known Remaining Registry Values")]
    [InlineData(false)]
    [InlineData(true)]
    public void ResumesKnownCompensation(bool movedOriginal)
    {
        using var fixture = new InstallerRecoveryFixture();
        var paths = MetadataPaths(fixture.Metadata);
        var originals = RuntimeFingerprint.CaptureFileEvidence(paths);
        RequireOriginalFiles(fixture.Metadata, fresh: false);
        fixture.InstallMetadata();
        var original = Assert.Single(fixture.Before.Files, file => file.Role == "uninstall" && file.Name == "unins000.exe");
        var after = InstallerSnapshotCodec.Decode(fixture.Observed!.After!, fixture.Metadata.Application);
        var replaced = after.Files.Single(file => file.Role == original.Role && file.Name == original.Name);
        var path = fixture.Backup.SourcePath(fixture.Metadata.Application, replaced);
        PhysicalObjectDeletion.DeleteVerified(new(path, replaced.ParentIdentity, replaced.Identity, FileObjectKind.File,
            replaced.SizeBytes, replaced.Sha256));
        if (movedOriginal)
            PhysicalObjectMove.MoveVerified(new(fixture.Backup.BackupPath(original), fixture.Prepared.BackupIdentity, original.Identity,
                fixture.Backup.SourcePath(fixture.Metadata.Application, original), original.ParentIdentity, FileObjectKind.File));
        using (var key = Registry.CurrentUser.OpenSubKey(fixture.Metadata.Paths.UninstallRegistryKey, writable: true))
            key!.DeleteValue("DisplayName");
        fixture.Recovery.RestoreCurrentMetadata(fixture.Metadata.Application, fixture.Transaction);
        Assert.Equal(originals, RuntimeFingerprint.CaptureFileEvidence(paths));
        RequireOriginalRegistry(fixture.Metadata, fresh: false);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Backup.DirectoryPath));
    }

    [Theory(DisplayName = "Unknown Metadata Changes Stop Compensation Before Any Known Target File Is Removed")]
    [InlineData("registry")]
    [InlineData("file")]
    [InlineData("replacement")]
    [InlineData("backup")]
    [InlineData("new-directory")]
    public void PreservesUnknownChanges(string fault)
    {
        using var fixture = new InstallerRecoveryFixture(); fixture.InstallMetadata();
        var engine = File.ReadAllBytes(fixture.Metadata.Engine);
        var message = Path.ChangeExtension(fixture.Metadata.Engine, "msg");
        var retained = Path.Combine(fixture.Metadata.Root, "retained-target.exe");
        var backup = fixture.Backup.BackupPath(fixture.Before.Files[0]);
        var desktopData = Path.Combine(fixture.Metadata.Paths.DesktopRoot, "user.txt");
        var privateWorker = Path.Combine(fixture.Metadata.Paths.ManagerRoot, InstallationAnchor.WorkerFile);
        if (fault == "registry")
        {
            using var key = Registry.CurrentUser.OpenSubKey(fixture.Metadata.Paths.UninstallRegistryKey, writable: true);
            key!.SetValue("Foreign", "Unknown retained value");
        }
        if (fault == "file") File.WriteAllText(message, "Unknown retained file");
        if (fault == "replacement")
        {
            File.Move(fixture.Metadata.Engine, retained);
            File.WriteAllBytes(fixture.Metadata.Engine, engine);
        }
        if (fault == "backup") File.WriteAllText(backup, "Unknown backup bytes");
        if (fault == "new-directory") File.WriteAllText(desktopData, "Retained user directory bytes");
        string[] watched = [fixture.Metadata.Engine, fixture.Metadata.Shortcut, privateWorker, message, retained, backup, desktopData];
        var files = RuntimeFingerprint.CaptureFileEvidence(watched);
        var registration = JsonSerializer.Serialize(InstallerMetadataInventory.CaptureRegistry(fixture.Metadata.Paths.UninstallRegistryKey));
        Assert.Throws<IOException>(() => fixture.Recovery.RestoreCurrentMetadata(fixture.Metadata.Application, fixture.Transaction));
        Assert.Equal(engine, File.ReadAllBytes(fixture.Metadata.Engine));
        Assert.Equal(files, RuntimeFingerprint.CaptureFileEvidence(watched));
        Assert.Equal(registration, JsonSerializer.Serialize(InstallerMetadataInventory.CaptureRegistry(fixture.Metadata.Paths.UninstallRegistryKey)));
        Assert.True(File.Exists(fixture.Metadata.Shortcut));
        Assert.True(File.Exists(privateWorker));
    }

    [Fact(DisplayName = "Installer Metadata Preservation Can Be Reversed Before Inno Has Changed Any Native Registration")]
    public void RestoresPreparedMetadata()
    {
        using var fixture = new InstallerRecoveryFixture();
        var metadata = fixture.Metadata;
        var paths = MetadataPaths(metadata);
        RequireOriginalFiles(metadata, fresh: false);
        var originals = RuntimeFingerprint.CaptureFileEvidence(paths);
        var known = originals.Where(file => file.Identity is not null).ToDictionary(file => Path.GetFileName(file.Path));
        fixture.Backup.Preposition(fixture.Metadata.Application, fixture.Transaction);
        string[] members = ["manager--identity.protected", "manager--nexus-installer-worker.exe", "uninstall--unins000.dat", "uninstall--unins000.exe"];
        Assert.Equal(members, Directory.EnumerateFileSystemEntries(fixture.Backup.DirectoryPath).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var member in members)
        {
            var original = known[member[(member.IndexOf("--", StringComparison.Ordinal) + 2)..]];
            Assert.False(File.Exists(original.Path));
            var preserved = Assert.Single(RuntimeFingerprint.CaptureFileEvidence(Path.Combine(fixture.Backup.DirectoryPath, member)));
            Assert.Equal(original.Identity, preserved.Identity);
            Assert.Equal(original.Bytes, preserved.Bytes);
        }
        RequireOriginalRegistry(metadata, fresh: false);
        for (var pass = 0; pass < 2; pass++)
        {
            fixture.Recovery.RestoreCurrentMetadata(metadata.Application, fixture.Transaction);
            Assert.Equal(originals, RuntimeFingerprint.CaptureFileEvidence(paths));
            RequireOriginalRegistry(metadata, fresh: false);
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Backup.DirectoryPath));
        }
    }

    [Fact(DisplayName = "Stopped Setup Recovery Rejects Still Live Source Processes Without Changing Target Metadata")]
    public void RequiresStoppedSetup()
    {
        using var fixture = new InstallerRecoveryFixture(); fixture.InstallMetadata();
        var bytes = File.ReadAllBytes(fixture.Metadata.Engine);
        Assert.Throws<IOException>(() => fixture.Recovery.RestoreStoppedMetadata(fixture.Metadata.Application, fixture.Transaction));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Metadata.Engine));
    }

    private static string[] MetadataPaths(InstallerMetadataFixture metadata) =>
    [
        metadata.Engine, Path.ChangeExtension(metadata.Engine, "dat"),
        Path.Combine(metadata.Paths.ManagerRoot, "identity.protected"),
        Path.Combine(metadata.Paths.ManagerRoot, "nexus-installer-worker.exe"),
        Path.ChangeExtension(metadata.Engine, "msg"), metadata.Shortcut,
        Path.Combine(metadata.Application, "NexusPipeline.uninstall.exe"),
        Path.Combine(metadata.Paths.GroupRoot, "NexusPipeline v0.18.0.lnk"),
        Path.Combine(metadata.Paths.GroupRoot, "NexusPipeline Uninstall.lnk")
    ];

    private static void RequireOriginalFiles(InstallerMetadataFixture metadata, bool fresh)
    {
        var paths = MetadataPaths(metadata);
        if (fresh) { Assert.All(paths, path => Assert.False(File.Exists(path), path)); return; }
        Assert.Equal("Controlled native installer engine bytes"u8.ToArray(), File.ReadAllBytes(paths[0]));
        Assert.Equal("Controlled native installer state"u8.ToArray(), File.ReadAllBytes(paths[1]));
        Assert.Equal("Controlled opaque seal bytes"u8.ToArray(), File.ReadAllBytes(paths[2]));
        Assert.Equal("Controlled worker bytes"u8.ToArray(), File.ReadAllBytes(paths[3]));
        Assert.All(paths.Skip(4), path => Assert.False(File.Exists(path), path));
    }

    private static void RequireOriginalRegistry(InstallerMetadataFixture metadata, bool fresh)
    {
        using var ownership = Registry.CurrentUser.OpenSubKey(metadata.Paths.RegistryKey);
        Assert.Null(ownership);
        using var uninstall = Registry.CurrentUser.OpenSubKey(metadata.Paths.UninstallRegistryKey);
        if (fresh) { Assert.Null(uninstall); return; }
        Assert.NotNull(uninstall);
        Assert.Equal(new[] { "InstallLocation", "UninstallString" }, uninstall.GetValueNames().Order(StringComparer.Ordinal));
        RequireRegistryValue(uninstall, "InstallLocation", RegistryValueKind.String, metadata.Application);
        RequireRegistryValue(uninstall, "UninstallString", RegistryValueKind.String, '"' + metadata.Engine + '"');
    }

    private static void RequireRegistryValue<T>(RegistryKey key, string name, RegistryValueKind kind, T expected)
    {
        Assert.Equal(kind, key.GetValueKind(name));
        Assert.Equal(expected, Assert.IsType<T>(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)));
    }
}
