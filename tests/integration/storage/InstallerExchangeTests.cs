using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerExchangeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletesTarget(bool initialized)
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized);
        var payload = Payload(fixture);
        var before = fixture.Scanner.Capture(fixture.Runtime.RuntimeRoot, default);
        var oldObjects = PhysicalTreeIdentity.Capture(fixture.Product.Application,
            ApplicationTree.Capture(fixture.Scanner, fixture.Product.Application, default));
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        var frame = Preserve(fixture);
        fixture.Product.Owner.ApplyTransition(frame.Root, frame.TransactionId, payload, Work(fixture));
        var identity = new InstallationAnchor(fixture.Product.Paths.ManagerRoot, fixture.Product.Paths.RegistryKey).Read(frame.Root);
        Assert.Equal(fixture.Identity, identity);
        Assert.False(Directory.Exists(fixture.Product.Checkpoint));
        Assert.False(Directory.Exists(InstallerPayloadTree.Workspace(frame)));
        Assert.True(before.Entries.SequenceEqual(fixture.Scanner.Capture(fixture.Runtime.RuntimeRoot, default).Entries));
        Assert.NotEqual(oldObjects.Single(item => item.RelativePath == ApplicationTree.HostEntry).Identity,
            PhysicalObjectIdentity.Read(Path.Combine(frame.Root, ApplicationTree.HostEntry)));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("extra-file")]
    [InlineData("extra-directory")]
    [InlineData("junction")]
    [InlineData("hardlink")]
    [InlineData("diagnostics")]
    public void RejectsInvalidStaging(string fault)
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: true);
        var payload = Payload(fixture);
        var readme = Path.Combine(payload, "README.md");
        var desktop = Path.Combine(payload, "resources", "desktop");
        var outside = Path.Combine(fixture.Product.Root, "outside");
        switch (fault)
        {
            case "missing": File.Delete(readme); break;
            case "changed": File.AppendAllText(readme, "Changed staging content"); break;
            case "extra-file": File.WriteAllText(Path.Combine(payload, "unknown"), "Keep this evidence"); break;
            case "extra-directory": Directory.CreateDirectory(Path.Combine(payload, "resources", "unknown")); break;
            case "junction": Directory.Move(desktop, outside); ControlledJunction.Create(desktop, outside); break;
            case "hardlink": Assert.True(CreateHardLink(outside, readme, IntPtr.Zero)); break;
        }
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        var frame = Preserve(fixture);
        try
        {
            var work = fault == "diagnostics" ? Path.Combine(payload, "inspection") : Work(fixture);
            Assert.ThrowsAny<IOException>(() => fixture.Product.Owner.ApplyTransition(frame.Root, frame.TransactionId, payload, work));
            Assert.False(Directory.Exists(new InstallerTargetTree(frame).DirectoryPath));
            fixture.Product.Owner.CancelTransition(frame.Root, frame.TransactionId, Work(fixture));
            var restored = ApplicationTree.Capture(fixture.Scanner, frame.Root, default);
            Assert.True(frame.OwnedSource!.ApplicationObjects.SequenceEqual(PhysicalTreeIdentity.Capture(frame.Root, restored)));
            Assert.False(Directory.Exists(fixture.Product.Checkpoint));
        }
        finally { if (fault == "junction") Directory.Delete(desktop); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RestoresTargetPrefix(int moved)
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: true);
        var payload = Payload(fixture);
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        var frame = Freeze(fixture, payload);
        MoveTargets(frame, moved);
        fixture.Product.Owner.CancelTransition(frame.Root, frame.TransactionId, Work(fixture));
        var snapshot = ApplicationTree.Capture(fixture.Scanner, frame.Root, default);
        Assert.True(frame.OwnedSource!.Application.Entries.SequenceEqual(snapshot.Entries));
        Assert.True(frame.OwnedSource.ApplicationObjects.SequenceEqual(PhysicalTreeIdentity.Capture(frame.Root, snapshot)));
        Assert.Equal(fixture.Identity, new InstallationAnchor(fixture.Product.Paths.ManagerRoot, fixture.Product.Paths.RegistryKey).Read(frame.Root));
        Assert.False(Directory.Exists(fixture.Product.Checkpoint));
        Assert.False(Directory.Exists(InstallerPayloadTree.Workspace(frame)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesForeignTarget(bool replace)
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: true);
        var payload = Payload(fixture);
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        var frame = Freeze(fixture, payload);
        MoveTargets(frame, 4);
        var target = Path.Combine(frame.Root, "README.md");
        if (replace)
        {
            var bytes = File.ReadAllBytes(target);
            File.Move(target, Path.Combine(fixture.Product.Root, "preserved-target.md"));
            File.WriteAllBytes(target, bytes);
        }
        else File.AppendAllText(target, "Preserve changed target bytes");
        var before = File.ReadAllBytes(target);
        Assert.Throws<IOException>(() => fixture.Product.Owner.CancelTransition(frame.Root, frame.TransactionId, Work(fixture)));
        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.True(Directory.Exists(fixture.Product.Checkpoint));
        Assert.True(File.Exists(Path.Combine(InstallerPayloadTree.Workspace(frame), ApplicationTree.HostEntry)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoresPublishedTarget(bool published)
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: true);
        var payload = Payload(fixture);
        var before = InstallerMetadataInventory.Capture(fixture.Product.Application, fixture.Product.Paths);
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        var frame = PublishFrame(fixture, payload, published, observed: false);
        fixture.Product.Owner.CancelTransition(frame.Root, frame.TransactionId, Work(fixture));
        var snapshot = ApplicationTree.Capture(fixture.Scanner, frame.Root, default);
        Assert.True(frame.OwnedSource!.Application.Entries.SequenceEqual(snapshot.Entries));
        Assert.True(frame.OwnedSource.ApplicationObjects.SequenceEqual(PhysicalTreeIdentity.Capture(frame.Root, snapshot)));
        Assert.True(InstallerMetadataInventory.Equal(before, InstallerMetadataInventory.Capture(frame.Root, fixture.Product.Paths)));
        Assert.False(Directory.Exists(fixture.Product.Checkpoint));
        Assert.False(Directory.Exists(InstallerPayloadTree.Workspace(frame)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ResumesCommittedCleanup(int removed)
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: true);
        var payload = Payload(fixture);
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        var frame = PublishFrame(fixture, payload, published: true, observed: true);
        new InstallerCheckpointStore(fixture.Product.Paths, "test").CommitTransition(frame);
        var original = InstallerApplicationObjects.Tree(frame.OwnedSource!.Application, frame.OwnedSource.ApplicationObjects);
        foreach (var name in ApplicationTree.Roots.Take(removed))
            InstallerApplicationObjects.DeleteRemaining(Path.Combine(InstallerPayloadTree.Workspace(frame), name),
                frame.PayloadIdentity!, ApplicationTree.Slice(original, name));
        fixture.Product.Owner.ApplyTransition(frame.Root, frame.TransactionId, payload, Work(fixture));
        var snapshot = ApplicationTree.Capture(fixture.Scanner, frame.Root, default);
        Assert.True(frame.TargetApplication!.Snapshot.Entries.SequenceEqual(snapshot.Entries));
        var expected = frame.TargetApplication.Objects.Where(item => item.RelativePath.Length > 0);
        Assert.True(expected.SequenceEqual(PhysicalTreeIdentity.Capture(frame.Root, snapshot).Where(item => item.RelativePath.Length > 0)));
        Assert.False(Directory.Exists(fixture.Product.Checkpoint));
        Assert.False(Directory.Exists(InstallerPayloadTree.Workspace(frame)));
    }

    internal static InstallerCheckpoint PublishFrame(InstallerOwnedTests.OwnedFixture fixture, string payload, bool published, bool observed)
    {
        var frame = Freeze(fixture, payload);
        MoveTargets(frame, 4);
        var before = InstallerSnapshotCodec.Decode(frame.Before, frame.Root);
        var identity = fixture.Identity with { ManifestSha256 = frame.ManifestSha256 };
        var publication = new InstallerIdentityPublication(fixture.Product.Paths);
        var target = publication.Stage(frame, identity, before);
        var store = new InstallerCheckpointStore(fixture.Product.Paths, "test");
        frame = store.Plan(frame.Root, frame.TransactionId, target);
        if (published) publication.Publish(frame);
        return observed ? store.Observe(frame.Root, frame.TransactionId) : frame;
    }

    internal static InstallerCheckpoint Freeze(InstallerOwnedTests.OwnedFixture fixture, string payload)
    {
        var frame = Preserve(fixture);
        var target = new InstallerTargetTree(frame).Stage(payload);
        return new InstallerCheckpointStore(fixture.Product.Paths, "test").FreezeTarget(frame.Root, frame.TransactionId, target);
    }

    internal static InstallerCheckpoint Preserve(InstallerOwnedTests.OwnedFixture fixture)
    {
        _ = fixture.Product.Owner.BeginTransition(fixture.Product.Application, fixture.Product.Transaction,
            fixture.Product.Target, InstallerRetainedFixture.Parent(), Work(fixture));
        fixture.Product.Owner.PreserveTransition(fixture.Product.Application, fixture.Product.Transaction, Work(fixture));
        return new InstallerCheckpointStore(fixture.Product.Paths, "test").Read(fixture.Product.Application, fixture.Product.Transaction);
    }

    internal static void MoveTargets(InstallerCheckpoint frame, int count)
    {
        var target = frame.TargetApplication!;
        var parent = target.Objects.Single(item => item.RelativePath.Length == 0).Identity;
        var directory = new InstallerTargetTree(frame).DirectoryPath;
        foreach (var name in ApplicationTree.Roots.Take(count))
            PhysicalObjectMove.MoveVerified(new(Path.Combine(directory, name), parent,
                target.Objects.Single(item => item.RelativePath == name).Identity, Path.Combine(frame.Root, name),
                frame.OwnedSource!.Identity.RootIdentity, name == "resources" ? FileObjectKind.Directory : FileObjectKind.File));
    }

    internal static string Payload(InstallerOwnedTests.OwnedFixture fixture)
    {
        var path = Path.Combine(fixture.Product.Root, "staged-payload");
        var snapshot = ApplicationTree.Capture(fixture.Scanner, fixture.Product.Application, default);
        foreach (var entry in snapshot.Entries.Where(entry => entry.Kind == FileObjectKind.File))
        {
            var target = Path.Combine(path, entry.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(fixture.Product.Application, entry.RelativePath), target);
        }
        return path;
    }

    private static string Work(InstallerOwnedTests.OwnedFixture fixture) => Path.Combine(fixture.Product.Root, "transition-inspection");

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string filename, string existingFilename, IntPtr reserved);
}
