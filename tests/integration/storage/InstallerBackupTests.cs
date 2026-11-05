using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerBackupTests
{
    [Fact(DisplayName = "Installer Preposition Moves Original Native Metadata Without Changing Its Bytes Or Physical Identities")]
    public void MovesNativeMetadata()
    {
        using var fixture = new InstallerMetadataFixture();
        fixture.WriteShortcut(Path.Combine(fixture.Application, ApplicationTree.HostEntry));
        var (store, backup, frame) = Create(fixture);
        var before = InstallerSnapshotCodec.Decode(frame.Before, fixture.Application);
        backup.Preposition(fixture.Application, frame.TransactionId);
        backup.Preposition(fixture.Application, frame.TransactionId);
        backup.RequirePreserved(fixture.Application, frame.TransactionId);
        foreach (var file in before.Files)
        {
            Assert.False(File.Exists(backup.SourcePath(fixture.Application, file)));
            Assert.Equal(file.Identity, PhysicalObjectIdentity.Read(backup.BackupPath(file)));
        }
        Assert.Equal(frame.Before, store.Read(fixture.Application, frame.TransactionId).Before);
        Assert.Equal(before.RootIdentity, PhysicalObjectIdentity.Read(fixture.Application));
    }

    [Theory(DisplayName = "Installer Preposition Resumes Only Recorded Original Objects Already Moved To Its Bound Backup Directory")]
    [InlineData(1)]
    [InlineData(2)]
    public void ResumesRecordedMoves(int count)
    {
        using var fixture = new InstallerMetadataFixture();
        var (_, backup, frame) = Create(fixture);
        var before = InstallerSnapshotCodec.Decode(frame.Before, fixture.Application);
        foreach (var file in before.Files.Take(count))
            PhysicalObjectMove.MoveVerified(new(backup.SourcePath(fixture.Application, file), file.ParentIdentity, file.Identity,
                backup.BackupPath(file), frame.BackupIdentity, FileObjectKind.File));
        backup.Preposition(fixture.Application, frame.TransactionId);
        backup.RequirePreserved(fixture.Application, frame.TransactionId);
    }

    [Fact(DisplayName = "An Unknown Backup Member Is Preserved And Stops Installer Preposition Before Original Objects Move")]
    public void PreservesUnknownBackup()
    {
        using var fixture = new InstallerMetadataFixture();
        var (_, backup, frame) = Create(fixture);
        var unknown = Path.Combine(fixture.Paths.ManagerRoot, "metadata-checkpoint", "before", "foreign.txt");
        File.WriteAllText(unknown, "Retained unknown metadata");
        Assert.Throws<IOException>(() => backup.Preposition(fixture.Application, frame.TransactionId));
        Assert.Equal("Retained unknown metadata", File.ReadAllText(unknown));
        Assert.True(File.Exists(fixture.Engine));
    }

    [Fact(DisplayName = "Changed Original Metadata Stays At Its Source And Cannot Enter The Frozen Installer Backup")]
    public void PreservesChangedSource()
    {
        using var fixture = new InstallerMetadataFixture();
        var (_, backup, frame) = Create(fixture);
        var file = InstallerSnapshotCodec.Decode(frame.Before, fixture.Application).Files[0];
        var source = backup.SourcePath(fixture.Application, file);
        File.WriteAllText(source, "Unknown changed original");
        Assert.Throws<IOException>(() => backup.Preposition(fixture.Application, frame.TransactionId));
        Assert.Equal("Unknown changed original", File.ReadAllText(source));
        Assert.False(File.Exists(backup.BackupPath(file)));
    }

    [Fact(DisplayName = "A Same Byte Replacement In The Installer Backup Does Not Acquire The Original Metadata Identity")]
    public void RejectsReplacedBackup()
    {
        using var fixture = new InstallerMetadataFixture();
        var (_, backup, frame) = Create(fixture);
        backup.Preposition(fixture.Application, frame.TransactionId);
        var file = InstallerSnapshotCodec.Decode(frame.Before, fixture.Application).Files[0];
        var path = backup.BackupPath(file); var original = File.ReadAllBytes(path);
        File.Move(path, Path.Combine(fixture.Root, "retained-original")); File.WriteAllBytes(path, original);
        Assert.Throws<IOException>(() => backup.RequirePreserved(fixture.Application, frame.TransactionId));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact(DisplayName = "A Live Read Lock Prevents Installer Metadata Movement And Preserves Its Original Namespace")]
    public void RejectsReadLocks()
    {
        using var fixture = new InstallerMetadataFixture();
        var (_, backup, frame) = Create(fixture);
        var file = InstallerSnapshotCodec.Decode(frame.Before, fixture.Application).Files[0];
        var path = backup.SourcePath(fixture.Application, file);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Throws<IOException>(() => backup.Preposition(fixture.Application, frame.TransactionId));
        Assert.Equal(file.Identity, PhysicalObjectIdentity.Read(path));
        Assert.False(File.Exists(backup.BackupPath(file)));
    }

    private static (InstallerCheckpointStore Store, InstallerMetadataBackup Backup, InstallerCheckpoint Frame) Create(InstallerMetadataFixture fixture)
    {
        var observed = ProcessSnapshotProbe.Capture(Environment.ProcessId, "", default);
        Assert.False(observed.Truncated);
        var setup = ProcessIdentityReader.Read(Assert.Single(observed.Processes).Ppid);
        var store = new InstallerCheckpointStore(fixture.Paths, "test");
        var frame = store.Create(fixture.Application, Guid.NewGuid(), new('a', 64), setup);
        return (store, new(fixture.Paths, store), frame);
    }
}
