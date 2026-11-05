using System.Runtime.Versioning;
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
        fixture.InstallMetadata();
        fixture.Recovery.RestoreCurrentMetadata(fixture.Metadata.Application, fixture.Transaction);
        fixture.Recovery.RestoreCurrentMetadata(fixture.Metadata.Application, fixture.Transaction);
        foreach (var file in fixture.Before.Files)
            Assert.Equal(file.Identity, PhysicalObjectIdentity.Read(fixture.Backup.SourcePath(fixture.Metadata.Application, file)));
        Assert.False(File.Exists(fixture.Metadata.Shortcut));
        using var ownership = Registry.CurrentUser.OpenSubKey(fixture.Metadata.Paths.RegistryKey);
        Assert.Null(ownership);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Backup.DirectoryPath));
    }

    [Fact(DisplayName = "Installer Compensation Restores Native Registry Types And Literal Values From The Frozen Original Snapshot")]
    public void RestoresTypedRegistry()
    {
        using var fixture = new InstallerRecoveryFixture(typedValues: true);
        fixture.InstallMetadata(typedValues: true);
        fixture.Recovery.RestoreCurrentMetadata(fixture.Metadata.Application, fixture.Transaction);
        var restored = InstallerMetadataInventory.Capture(fixture.Metadata.Application, fixture.Metadata.Paths);
        Assert.True(InstallerMetadataInventory.Equal(fixture.Before, restored));
    }

    [Theory(DisplayName = "Partial Installer Metadata Compensation Resumes With Original File Identities And Known Remaining Registry Values")]
    [InlineData(false)]
    [InlineData(true)]
    public void ResumesKnownCompensation(bool movedOriginal)
    {
        using var fixture = new InstallerRecoveryFixture(); fixture.InstallMetadata();
        var original = fixture.Before.Files[0];
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
        Assert.True(InstallerMetadataInventory.Equal(fixture.Before,
            InstallerMetadataInventory.Capture(fixture.Metadata.Application, fixture.Metadata.Paths)));
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
        if (fault == "registry")
        {
            using var key = Registry.CurrentUser.OpenSubKey(fixture.Metadata.Paths.UninstallRegistryKey, writable: true);
            key!.SetValue("Foreign", "Unknown retained value");
        }
        if (fault == "file") File.WriteAllText(Path.ChangeExtension(fixture.Metadata.Engine, "msg"), "Unknown retained file");
        if (fault == "replacement")
        {
            File.Move(fixture.Metadata.Engine, Path.Combine(fixture.Metadata.Root, "retained-target.exe"));
            File.WriteAllBytes(fixture.Metadata.Engine, engine);
        }
        if (fault == "backup") File.WriteAllText(fixture.Backup.BackupPath(fixture.Before.Files[0]), "Unknown backup bytes");
        if (fault == "new-directory") File.WriteAllText(Path.Combine(fixture.Metadata.Paths.DesktopRoot, "user.txt"), "Retained user directory bytes");
        Assert.Throws<IOException>(() => fixture.Recovery.RestoreCurrentMetadata(fixture.Metadata.Application, fixture.Transaction));
        Assert.Equal(engine, File.ReadAllBytes(fixture.Metadata.Engine));
        Assert.True(File.Exists(fixture.Metadata.Shortcut));
        Assert.True(File.Exists(Path.Combine(fixture.Metadata.Paths.ManagerRoot, InstallationAnchor.WorkerFile)));
    }

    [Fact(DisplayName = "Installer Metadata Preservation Can Be Reversed Before Inno Has Changed Any Native Registration")]
    public void RestoresPreparedMetadata()
    {
        using var fixture = new InstallerRecoveryFixture();
        fixture.Backup.Preposition(fixture.Metadata.Application, fixture.Transaction);
        fixture.Recovery.RestoreCurrentMetadata(fixture.Metadata.Application, fixture.Transaction);
        Assert.True(InstallerMetadataInventory.Equal(fixture.Before,
            InstallerMetadataInventory.Capture(fixture.Metadata.Application, fixture.Metadata.Paths)));
    }

    [Fact(DisplayName = "Stopped Setup Recovery Rejects Still Live Source Processes Without Changing Target Metadata")]
    public void RequiresStoppedSetup()
    {
        using var fixture = new InstallerRecoveryFixture(); fixture.InstallMetadata();
        var bytes = File.ReadAllBytes(fixture.Metadata.Engine);
        Assert.Throws<IOException>(() => fixture.Recovery.RestoreStoppedMetadata(fixture.Metadata.Application, fixture.Transaction));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Metadata.Engine));
    }
}
