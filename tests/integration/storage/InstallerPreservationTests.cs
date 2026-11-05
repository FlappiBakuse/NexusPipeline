using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerPreservationTests
{
    [Theory(DisplayName = "Active Setup Preserves Every Original Application Object And Cancels Without Touching Runtime Or Native Registration")]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesOriginal(bool initialized)
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized);
        var runtime = fixture.Scanner.Capture(fixture.Runtime.RuntimeRoot, default);
        var metadata = InstallerMetadataInventory.Capture(fixture.Product.Application, fixture.Product.Paths);
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        _ = Begin(fixture);
        fixture.Product.Owner.PreserveTransition(fixture.Product.Application, fixture.Product.Transaction, Work(fixture));
        var frame = Read(fixture);
        Assert.Equal("payload", frame.Stage); Assert.NotNull(frame.PayloadIdentity);
        foreach (var name in ApplicationTree.Roots)
        {
            Assert.False(File.Exists(Path.Combine(fixture.Product.Application, name)) || Directory.Exists(Path.Combine(fixture.Product.Application, name)));
            Assert.Equal(frame.OwnedSource!.ApplicationObjects.Single(item => item.RelativePath == name).Identity,
                PhysicalObjectIdentity.Read(Path.Combine(InstallerPayloadTree.Workspace(frame), name)));
        }
        fixture.Product.Owner.PreserveTransition(fixture.Product.Application, fixture.Product.Transaction, Work(fixture));
        fixture.Product.Owner.CancelTransition(fixture.Product.Application, fixture.Product.Transaction, Work(fixture));
        AssertRestored(fixture, frame);
        Assert.True(runtime.Entries.SequenceEqual(fixture.Scanner.Capture(fixture.Runtime.RuntimeRoot, default).Entries));
        Assert.True(InstallerMetadataInventory.Equal(metadata, InstallerMetadataInventory.Capture(fixture.Product.Application, fixture.Product.Paths)));
    }

    [Theory(DisplayName = "Every Interrupted Original Application Move Prefix Resumes To The Same Preserved Physical Objects")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ResumesPreservation(int moved)
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: true);
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        _ = Begin(fixture);
        var store = new InstallerCheckpointStore(fixture.Product.Paths, "test");
        var frame = store.PreparePayload(fixture.Product.Application, fixture.Product.Transaction);
        foreach (var name in ApplicationTree.Roots.Take(moved)) Move(frame, name, preserving: true);
        fixture.Product.Owner.PreserveTransition(fixture.Product.Application, fixture.Product.Transaction, Work(fixture));
        Assert.All(ApplicationTree.Roots, name => Assert.True(File.Exists(Path.Combine(InstallerPayloadTree.Workspace(frame), name)) ||
            Directory.Exists(Path.Combine(InstallerPayloadTree.Workspace(frame), name))));
        fixture.Product.Owner.CancelTransition(fixture.Product.Application, fixture.Product.Transaction, Work(fixture));
        AssertRestored(fixture, frame);
    }

    [Theory(DisplayName = "Every Interrupted Original Restoration Prefix Returns The Original Application And Removes Only Its Owned Workspace")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ResumesRestoration(int moved)
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: true);
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        _ = Begin(fixture);
        fixture.Product.Owner.PreserveTransition(fixture.Product.Application, fixture.Product.Transaction, Work(fixture));
        var frame = Read(fixture);
        foreach (var name in ApplicationTree.Roots.Take(moved)) Move(frame, name, preserving: false);
        new InstallerPayloadTree(frame).RestoreOriginal();
        new InstallerPayloadTree(frame).RestoreOriginal();
        new InstallerPayloadTree(frame).CompleteRestoration();
        new InstallerPayloadTree(frame).CompleteRestoration();
        fixture.Product.Owner.CancelTransition(fixture.Product.Application, fixture.Product.Transaction, Work(fixture));
        AssertRestored(fixture, frame);
    }

    [Theory(DisplayName = "Changed Bytes Replaced Objects Duplicated Paths And Foreign Workspace Files Preserve The Entire Scene Before Restoration")]
    [InlineData("installed")]
    [InlineData("preserved")]
    [InlineData("identity")]
    [InlineData("workspace")]
    [InlineData("duplicate")]
    public void PreservesUnknownScene(string fault)
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: true);
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        _ = Begin(fixture);
        fixture.Product.Owner.PreserveTransition(fixture.Product.Application, fixture.Product.Transaction, Work(fixture));
        var frame = Read(fixture); var workspace = InstallerPayloadTree.Workspace(frame);
        var preserved = Path.Combine(workspace, "README.md");
        if (fault == "installed") File.WriteAllText(Path.Combine(fixture.Product.Application, "README.md"), "Preserve unknown installed bytes");
        if (fault == "preserved") File.AppendAllText(preserved, "Preserve changed original bytes");
        if (fault == "identity")
        {
            var bytes = File.ReadAllBytes(preserved);
            File.Move(preserved, Path.Combine(fixture.Product.Root, "retained-original.md")); File.WriteAllBytes(preserved, bytes);
        }
        if (fault == "workspace") File.WriteAllText(Path.Combine(workspace, "unknown.txt"), "Preserve foreign workspace bytes");
        if (fault == "duplicate") File.Copy(preserved, Path.Combine(fixture.Product.Application, "README.md"));
        var before = fixture.Scanner.Capture(workspace, default);
        var checkpoint = File.ReadAllBytes(Path.Combine(fixture.Product.Checkpoint, "payload.protected"));
        Assert.Throws<IOException>(() => fixture.Product.Owner.CancelTransition(fixture.Product.Application, fixture.Product.Transaction, Work(fixture)));
        Assert.True(before.Entries.SequenceEqual(fixture.Scanner.Capture(workspace, default).Entries));
        Assert.Equal(checkpoint, File.ReadAllBytes(Path.Combine(fixture.Product.Checkpoint, "payload.protected")));
        Assert.False(File.Exists(Path.Combine(fixture.Product.Application, ApplicationTree.HostEntry)));
    }

    [Theory(DisplayName = "Application Preservation Refuses Unknown Or Replaced Workspaces And An Existing Runtime Lease")]
    [InlineData("existing")]
    [InlineData("replaced")]
    [InlineData("runtime")]
    public void RequiresWorkspaceOwnership(string fault)
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: true);
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        var frame = Begin(fixture); var workspace = InstallerPayloadTree.Workspace(frame);
        if (fault == "existing")
        {
            Directory.CreateDirectory(workspace); File.WriteAllText(Path.Combine(workspace, "unknown.txt"), "Keep preexisting data");
        }
        else
        {
            frame = new InstallerCheckpointStore(fixture.Product.Paths, "test").PreparePayload(fixture.Product.Application, fixture.Product.Transaction);
            if (fault == "replaced")
            {
                Directory.Move(workspace, workspace + "-retained"); Directory.CreateDirectory(workspace);
            }
        }
        using var lease = fault == "runtime" ? InstanceLease.AcquireInstaller(fixture.Runtime, new(), InstallerRetainedFixture.Parent()) : null;
        var before = ApplicationTree.Capture(fixture.Scanner, fixture.Product.Application, default);
        Assert.Throws<IOException>(() => fixture.Product.Owner.PreserveTransition(fixture.Product.Application, fixture.Product.Transaction, Work(fixture)));
        Assert.True(before.Entries.SequenceEqual(ApplicationTree.Capture(fixture.Scanner, fixture.Product.Application, default).Entries));
    }

    [Fact(DisplayName = "Metadata Checkpoint Successors Cannot Remove The Frozen Application Workspace Identity")]
    public void BindsPayloadCheckpoint()
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: false);
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        _ = Begin(fixture);
        var store = new InstallerCheckpointStore(fixture.Product.Paths, "test");
        _ = store.PreparePayload(fixture.Product.Application, fixture.Product.Transaction);
        var planned = store.Plan(fixture.Product.Application, fixture.Product.Transaction, store.Capture(fixture.Product.Application));
        var bytes = InstallerCheckpointCodec.Seal(planned with { PayloadIdentity = null });
        var path = Path.Combine(fixture.Product.Checkpoint, "planned.protected"); File.WriteAllBytes(path, bytes);
        Assert.Throws<IOException>(() => store.Read(fixture.Product.Application, fixture.Product.Transaction));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private static void Move(InstallerCheckpoint frame, string name, bool preserving)
    {
        var original = frame.OwnedSource!.ApplicationObjects.Single(item => item.RelativePath == name).Identity;
        var current = Path.Combine(frame.Root, name); var before = Path.Combine(InstallerPayloadTree.Workspace(frame), name);
        PhysicalObjectMove.MoveVerified(new(preserving ? current : before, preserving ? frame.OwnedSource.Identity.RootIdentity : frame.PayloadIdentity!,
            original, preserving ? before : current, preserving ? frame.PayloadIdentity! : frame.OwnedSource.Identity.RootIdentity,
            name == "resources" ? FileObjectKind.Directory : FileObjectKind.File));
    }

    private static void AssertRestored(InstallerOwnedTests.OwnedFixture fixture, InstallerCheckpoint frame)
    {
        var source = frame.OwnedSource!;
        Assert.False(Directory.Exists(InstallerPayloadTree.Workspace(frame))); Assert.False(Directory.Exists(fixture.Product.Checkpoint));
        var snapshot = ApplicationTree.Capture(fixture.Scanner, fixture.Product.Application, default);
        Assert.True(source.Application.Entries.SequenceEqual(snapshot.Entries));
        Assert.True(source.ApplicationObjects.SequenceEqual(PhysicalTreeIdentity.Capture(fixture.Product.Application, snapshot)));
        Assert.Equal(fixture.Identity, new InstallationAnchor(fixture.Product.Paths.ManagerRoot, fixture.Product.Paths.RegistryKey).Read(fixture.Product.Application));
    }

    private static string Work(InstallerOwnedTests.OwnedFixture fixture) => Path.Combine(fixture.Product.Root, "preservation-inspection");
    private static InstallerCheckpoint Begin(InstallerOwnedTests.OwnedFixture fixture) => fixture.Product.Owner.BeginTransition(
        fixture.Product.Application, fixture.Product.Transaction, fixture.Product.Target, InstallerRetainedFixture.Parent(), Work(fixture));
    private static InstallerCheckpoint Read(InstallerOwnedTests.OwnedFixture fixture) => new InstallerCheckpointStore(fixture.Product.Paths, "test")
        .Read(fixture.Product.Application, fixture.Product.Transaction);
}
