using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerTransitionTests
{
    [Theory(DisplayName = "Active Setup Preparation Persists Its Complete Original Source And Independent Target Without Changing Application Or Runtime")]
    [InlineData(false)]
    [InlineData(true)]
    public void FreezesTransition(bool initialized)
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized);
        var product = fixture.Product;
        var application = ApplicationTree.Capture(fixture.Scanner, product.Application, default);
        var runtime = fixture.Scanner.Capture(fixture.Runtime.RuntimeRoot, default);
        var metadata = InstallerMetadataInventory.Capture(product.Application, product.Paths);
        using var gate = InstallerRuntimeGate.Acquire(product.Application);
        var frame = Begin(fixture);
        Assert.Equal(fixture.Identity, frame.OwnedSource!.Identity);
        Assert.True(InstallerMetadataInventory.Equal(metadata, frame.OwnedSource.Metadata));
        Assert.Equal(product.Target.Bytes.ToArray(), frame.TargetManifest);
        Assert.True(application.Entries.SequenceEqual(frame.OwnedSource.Application.Entries));
        Assert.True(runtime.Entries.SequenceEqual(fixture.Scanner.Capture(fixture.Runtime.RuntimeRoot, default).Entries));
        Assert.True(InstallerMetadataInventory.Equal(metadata, InstallerMetadataInventory.Capture(product.Application, product.Paths)));
        var store = new InstallerCheckpointStore(product.Paths, "test");
        Assert.True(InstallerSourceBinding.Equal(frame.OwnedSource, store.ReadPending(product.Application).OwnedSource));
        Assert.True(InstallerSourceBinding.Equal(frame.OwnedSource, Begin(fixture).OwnedSource));
        frame.OwnedSource.Metadata.Files[0] = frame.OwnedSource.Metadata.Files[0] with { Sha256 = new('a', 64) };
        Assert.True(InstallerMetadataInventory.Equal(metadata, store.Read(product.Application, product.Transaction).OwnedSource!.Metadata));
    }

    [Theory(DisplayName = "Active Setup Preparation Rejects A Source With A Different Identity Manifest Process Profile Or Physical File Set")]
    [InlineData("format")]
    [InlineData("version")]
    [InlineData("profile")]
    [InlineData("owner")]
    [InlineData("metadata")]
    [InlineData("manifest")]
    [InlineData("objects")]
    [InlineData("identity")]
    public void RejectsInvalidSource(string fault)
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: false);
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        var frame = Begin(fixture); var source = frame.OwnedSource!;
        var changed = fault switch
        {
            "format" => source with { Format = "unknown" }, "version" => source with { Version = 2 },
            "profile" => source with { Profile = "production" },
            "owner" => source with { SetupOwner = source.SetupOwner with { ProcessId = source.SetupOwner.ProcessId + 1 } },
            "metadata" => source with { Metadata = source.Metadata with { Files = [] } },
            "manifest" => source with { Identity = source.Identity with { ManifestSha256 = new('a', 64) } },
            "objects" => source with { ApplicationObjects = source.ApplicationObjects.Skip(1).ToArray() },
            "identity" => source with { Identity = source.Identity with { ProductVersion = "0.17.6" } },
            _ => throw new ArgumentException(fault)
        };
        var path = Path.Combine(fixture.Product.Checkpoint, "prepared.protected"); var original = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => InstallerCheckpointCodec.Seal(frame with { OwnedSource = changed }));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory(DisplayName = "Active Setup Preparation Requires Its Startup Guard Exclusive Runtime And Unchanged Source Before Any Checkpoint")]
    [InlineData("guard")]
    [InlineData("runtime")]
    [InlineData("application")]
    [InlineData("work")]
    public void RequiresSourceAdmission(string fault)
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: true);
        using var running = fault == "runtime" ? InstanceLease.Acquire(fixture.Runtime, new()) : null;
        using var gate = fault == "guard" ? null : InstallerRuntimeGate.Acquire(fixture.Product.Application);
        if (fault == "application") File.AppendAllText(Path.Combine(fixture.Product.Application, "README.md"), "Unknown application bytes");
        var work = fault == "work" ? Path.Combine(fixture.Runtime.RuntimeRoot, "inspection") : Work(fixture);
        Assert.Throws<IOException>(() => fixture.Product.Owner.BeginTransition(fixture.Product.Application, fixture.Product.Transaction,
            fixture.Product.Target, InstallerRetainedFixture.Parent(), work));
        Assert.False(Directory.Exists(fixture.Product.Checkpoint));
    }

    [Theory(DisplayName = "Active Setup Replay Rejects Changed Transaction Target Or Source And Preserves Original Prepared Evidence")]
    [InlineData("transaction")]
    [InlineData("target")]
    [InlineData("source")]
    public void PreservesChangedReplay(string fault)
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: false);
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        _ = Begin(fixture);
        var path = Path.Combine(fixture.Product.Checkpoint, "prepared.protected"); var original = File.ReadAllBytes(path);
        var target = fixture.Product.Target;
        if (fault == "target")
        {
            var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(target.Bytes.Span) + " ");
            target = InstallerTargetManifest.Read(bytes, InstallerIdentityFixture.Digest(bytes), "test");
        }
        if (fault == "source") File.AppendAllText(Path.Combine(fixture.Product.Application, "README.md"), "Preserve changed source");
        Assert.Throws<IOException>(() => fixture.Product.Owner.BeginTransition(fixture.Product.Application,
            fault == "transaction" ? Guid.NewGuid() : fixture.Product.Transaction, target, InstallerRetainedFixture.Parent(), Work(fixture)));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact(DisplayName = "Checkpoint Successors Cannot Drop The Original Active Installation Source")]
    public void PreservesSourceChain()
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: false);
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        _ = Begin(fixture);
        var store = new InstallerCheckpointStore(fixture.Product.Paths, "test");
        var planned = store.Plan(fixture.Product.Application, fixture.Product.Transaction, store.Capture(fixture.Product.Application));
        var changed = InstallerCheckpointCodec.Seal(planned with { OwnedSource = null });
        var path = Path.Combine(fixture.Product.Checkpoint, "planned.protected"); File.WriteAllBytes(path, changed);
        Assert.Throws<IOException>(() => store.Read(fixture.Product.Application, fixture.Product.Transaction));
        Assert.Equal(changed, File.ReadAllBytes(path));
    }

    [Theory(DisplayName = "Cancelling An Unwritten Active Setup Restores Its Original Metadata And Removes Only The Owned Checkpoint")]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelsUnwrittenTransition(bool initialized)
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized);
        var metadata = InstallerMetadataInventory.Capture(fixture.Product.Application, fixture.Product.Paths);
        var application = ApplicationTree.Capture(fixture.Scanner, fixture.Product.Application, default);
        var runtime = fixture.Scanner.Capture(fixture.Runtime.RuntimeRoot, default);
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        _ = Begin(fixture);
        fixture.Product.Owner.CancelTransition(fixture.Product.Application, fixture.Product.Transaction, Work(fixture));
        Assert.False(Directory.Exists(fixture.Product.Checkpoint));
        Assert.True(InstallerMetadataInventory.Equal(metadata, InstallerMetadataInventory.Capture(fixture.Product.Application, fixture.Product.Paths)));
        Assert.True(application.Entries.SequenceEqual(ApplicationTree.Capture(fixture.Scanner, fixture.Product.Application, default).Entries));
        Assert.True(runtime.Entries.SequenceEqual(fixture.Scanner.Capture(fixture.Runtime.RuntimeRoot, default).Entries));
        Assert.Equal(fixture.Identity, new InstallationAnchor(fixture.Product.Paths.ManagerRoot, fixture.Product.Paths.RegistryKey).Read(fixture.Product.Application));
    }

    [Theory(DisplayName = "Active Setup Cancellation Preserves Changed Application Runtime And Native Metadata Instead Of Removing Evidence")]
    [InlineData("application")]
    [InlineData("runtime")]
    [InlineData("native")]
    public void PreservesChangedCancellation(string fault)
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: true);
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        _ = Begin(fixture);
        var path = Path.Combine(fixture.Product.Checkpoint, "prepared.protected"); var original = File.ReadAllBytes(path);
        if (fault == "application") File.AppendAllText(Path.Combine(fixture.Product.Application, "README.md"), "Keep changed program");
        if (fault == "runtime")
        {
            var instancePath = Path.Combine(fixture.Runtime.RuntimeRoot, "instance.json");
            var instance = JsonNode.Parse(File.ReadAllBytes(instancePath))!.AsObject();
            instance["instanceId"] = Guid.NewGuid().ToString("D");
            File.WriteAllText(instancePath, instance.ToJsonString());
        }
        if (fault == "native")
        {
            using var key = Registry.CurrentUser.OpenSubKey(fixture.Product.Paths.UninstallRegistryKey, writable: true);
            key!.SetValue("DisplayVersion", "0.18.9");
        }
        Assert.Throws<IOException>(() => fixture.Product.Owner.CancelTransition(fixture.Product.Application, fixture.Product.Transaction, Work(fixture)));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact(DisplayName = "The Actual Published Installer Worker Persists Replays And Cancels An Active Source With The Same Target And Runtime")]
    public async Task PreparesThroughEntry()
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: true);
        var product = fixture.Product; var runtime = fixture.Scanner.Capture(fixture.Runtime.RuntimeRoot, default);
        using var gate = InstallerRuntimeGate.Acquire(product.Application);
        string[] arguments = ["installer-state", "begin-transition", "--root", product.Application,
            "--transaction", product.Transaction.ToString("D"), "--manifest", product.Digest, "--manifest-file", product.TargetPath, "--work-root", Work(fixture)];
        var prepared = await product.Execute(arguments); Assert.Equal(0, prepared.ExitCode);
        using var document = JsonDocument.Parse(prepared.Output);
        Assert.Equal("nexus.installer.transition-preparation", document.RootElement.GetProperty("format").GetString());
        Assert.Equal(fixture.Identity.InstallationId, document.RootElement.GetProperty("installationId").GetGuid());
        Assert.Equal(fixture.Identity.ManifestSha256, document.RootElement.GetProperty("sourceManifestSha256").GetString());
        Assert.Equal(product.Digest, document.RootElement.GetProperty("targetManifestSha256").GetString());
        var checkpoint = File.ReadAllBytes(Path.Combine(product.Checkpoint, "prepared.protected"));
        Assert.Equal(0, (await product.Execute(arguments)).ExitCode);
        Assert.Equal(checkpoint, File.ReadAllBytes(Path.Combine(product.Checkpoint, "prepared.protected")));
        Assert.Equal(0, (await product.Execute("installer-state", "cancel-transition", "--root", product.Application,
            "--transaction", product.Transaction.ToString("D"), "--work-root", Work(fixture))).ExitCode);
        Assert.False(Directory.Exists(product.Checkpoint));
        Assert.True(runtime.Entries.SequenceEqual(fixture.Scanner.Capture(fixture.Runtime.RuntimeRoot, default).Entries));
    }

    [Fact(DisplayName = "Cancelling An Unwritten Active Setup Preserves User Data Created After Source Preparation")]
    public void PreservesNewData()
    {
        using var fixture = new InstallerOwnedTests.OwnedFixture(initialized: true);
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        _ = Begin(fixture);
        var path = Path.Combine(fixture.Runtime.RuntimeRoot, "new-user-data.bin");
        var bytes = "Preserve independently created user data"u8.ToArray(); File.WriteAllBytes(path, bytes);
        var identity = PhysicalObjectIdentity.Read(path);
        fixture.Product.Owner.CancelTransition(fixture.Product.Application, fixture.Product.Transaction, Work(fixture));
        Assert.False(Directory.Exists(fixture.Product.Checkpoint));
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Equal(identity, PhysicalObjectIdentity.Read(path));
    }

    private static string Work(InstallerOwnedTests.OwnedFixture fixture) => Path.Combine(fixture.Product.Root, "transition-inspection");
    private static InstallerCheckpoint Begin(InstallerOwnedTests.OwnedFixture fixture) => fixture.Product.Owner.BeginTransition(
        fixture.Product.Application, fixture.Product.Transaction, fixture.Product.Target, InstallerRetainedFixture.Parent(), Work(fixture));
}
