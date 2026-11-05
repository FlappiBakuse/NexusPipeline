using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Security;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerDataTests
{
    [Theory(DisplayName = "Explicit Uninstall Deletes Only Its Frozen Owned Runtime And Retains Proof Until The Native Engine Finishes")]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovesOwnedRuntime(bool initialized)
    {
        using var fixture = new InstallerUninstallFixture();
        if (initialized) fixture.Initialize();
        var outside = Path.Combine(fixture.Product.Application, "user-preservation.txt");
        File.WriteAllText(outside, "Retained bytes outside runtime");
        if (initialized) File.WriteAllBytes(Path.Combine(fixture.Instance.RuntimeRoot, "user-data.bin"), [11, 17, 23]);
        using var guard = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        using (new FileStream(Path.Combine(fixture.Product.Application, "uninstall/unins000.dat"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fixture.Owner.DeleteData(fixture.Product.Application, Guid.NewGuid(), fixture.Native, fixture.WorkRoot);
            AssertRemoved(fixture);
            Assert.True(fixture.Owner.Check(fixture.Product.Application, fixture.Native).PendingData);
            Assert.Throws<IOException>(() => fixture.Owner.KeepData(fixture.Product.Application, Guid.NewGuid(), fixture.Native, fixture.WorkRoot));
            fixture.Owner.DeleteData(fixture.Product.Application, Guid.NewGuid(), fixture.Native, fixture.WorkRoot);
            AssertRemoved(fixture);
        }
        Assert.True(File.Exists(Path.Combine(fixture.Product.Application, ApplicationTree.HostEntry)));
        Assert.True(File.Exists(Path.Combine(fixture.Product.Paths.ManagerRoot, InstallationAnchor.IdentityFile)));
        NativeRemoved(fixture);
        fixture.Owner.FinishData(fixture.Product.Application, Guid.NewGuid(), fixture.Native);
        Assert.False(Directory.Exists(fixture.Product.Paths.ManagerRoot));
        Assert.False(InstallerMetadataInventory.CaptureRegistry(fixture.Product.Paths.RegistryKey).Exists);
        Assert.Equal("Retained bytes outside runtime", File.ReadAllText(outside));
    }

    [Fact(DisplayName = "Explicit Data Removal Completes The Existing Durable Startup Removal Before Deleting Its Runtime")]
    public void OrdersStartupRemoval()
    {
        using var fixture = new InstallerUninstallFixture(); fixture.Initialize(autoStart: true);
        fixture.Startup.BeforeMutation = () => Assert.True(Directory.Exists(fixture.Instance.RuntimeRoot));
        using var guard = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        fixture.Owner.DeleteData(fixture.Product.Application, Guid.NewGuid(), fixture.Native, fixture.WorkRoot);
        AssertRemoved(fixture);
        Assert.Equal(1, fixture.Startup.Deletes); Assert.False(File.Exists(fixture.Startup.Path));
    }

    [Fact(DisplayName = "Unknown Startup Outcomes Preserve Runtime And Installation Proof Before Any Data Deletion Intent")]
    public void PreservesStartupUnknown()
    {
        using var fixture = new InstallerUninstallFixture(); fixture.Initialize(autoStart: true);
        fixture.Startup.BeforeMutation = () => throw new IOException("controlled_outcome_unknown");
        var path = Path.Combine(fixture.Product.Paths.ManagerRoot, InstallationAnchor.IdentityFile); var bytes = File.ReadAllBytes(path);
        using var guard = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        Assert.Throws<IOException>(() => fixture.Owner.DeleteData(fixture.Product.Application, Guid.NewGuid(), fixture.Native, fixture.WorkRoot));
        Assert.True(Directory.Exists(fixture.Instance.RuntimeRoot)); Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(new InstallerDataStore(fixture.Product.Paths, "test").Exists);
    }

    [Theory(DisplayName = "Data Deletion Requires Native Guard Runtime Exclusivity And Recognized Source Inputs Before Effects")]
    [InlineData("guard")]
    [InlineData("lease")]
    [InlineData("database")]
    [InlineData("payload")]
    [InlineData("manager")]
    [InlineData("native")]
    public void PreservesUnsafeInputs(string fault)
    {
        using var fixture = new InstallerUninstallFixture();
        if (fault == "database") { fixture.Initialize(); File.WriteAllText(Path.Combine(fixture.Instance.RuntimeRoot, "state/nexus.db"), "Unknown database"); }
        if (fault == "payload") File.WriteAllText(Path.Combine(fixture.Product.Application, "README.md"), "Unknown payload bytes");
        if (fault == "manager") File.WriteAllText(Path.Combine(fixture.Product.Paths.ManagerRoot, "foreign.txt"), "Unknown manager bytes");
        if (fault == "native") fixture.Save(fixture.Identity with { NativeEngine = fixture.Identity.NativeEngine with { Sha256 = new('b', 64) } });
        var before = Snapshot(fixture.Instance.RuntimeRoot);
        using var guard = fault == "guard" ? null : InstallerRuntimeGate.Acquire(fixture.Product.Application);
        using var lease = fault == "lease" ? InstanceLease.AcquireInstaller(fixture.Instance, new(), fixture.Native) : null;
        Assert.Throws<IOException>(() => fixture.Owner.DeleteData(fixture.Product.Application, Guid.NewGuid(), fixture.Native, fixture.WorkRoot));
        lease?.Dispose();
        Assert.Equal(before, Snapshot(fixture.Instance.RuntimeRoot));
        Assert.False(new InstallerDataStore(fixture.Product.Paths, "test").Exists);
    }

    [Fact(DisplayName = "An Interrupted Exact Object Deletion Prefix Resumes From Its Protected Frozen Intent")]
    public void ResumesKnownPrefix()
    {
        using var fixture = new InstallerUninstallFixture();
        using var guard = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        var intent = Prepare(fixture); Relocate(fixture);
        var entry = intent.Runtime.Entries.Single(entry => entry.RelativePath == "instance.lock");
        var physical = intent.Objects.Single(item => item.RelativePath == entry.RelativePath).Identity;
        PhysicalObjectDeletion.DeleteVerified(new(Path.Combine(InstallerRuntimeRemoval.Retired(fixture.Product.Application), entry.RelativePath),
            fixture.Identity.RuntimeIdentity, physical, entry.Kind, entry.SizeBytes, entry.Sha256));
        fixture.Owner.DeleteData(fixture.Product.Application, Guid.NewGuid(), fixture.Native, fixture.WorkRoot);
        AssertRemoved(fixture);
    }

    [Theory(DisplayName = "Remaining Data Must Match The Frozen Object Identity And Bytes Before Any Further Deletion")]
    [InlineData("added")]
    [InlineData("changed")]
    [InlineData("replacement")]
    public void PreservesChangedObjects(string fault)
    {
        using var fixture = new InstallerUninstallFixture();
        using var guard = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        _ = Prepare(fixture); Relocate(fixture);
        var retired = InstallerRuntimeRemoval.Retired(fixture.Product.Application);
        var path = Path.Combine(retired, "instance.json");
        if (fault == "added") File.WriteAllText(Path.Combine(retired, "foreign.txt"), "Unknown retained bytes");
        if (fault == "changed") File.AppendAllText(path, "changed");
        if (fault == "replacement") { var bytes = File.ReadAllBytes(path); File.Move(path, Path.Combine(fixture.Product.Root, "original.json")); File.WriteAllBytes(path, bytes); }
        var before = Snapshot(retired);
        Assert.Throws<IOException>(() => fixture.Owner.DeleteData(fixture.Product.Application, Guid.NewGuid(), fixture.Native, fixture.WorkRoot));
        Assert.Equal(before, Snapshot(retired));
    }

    [Theory(DisplayName = "Unknown Protected Deletion Plans And Names Stay In Place Without Removing Runtime Bytes")]
    [InlineData("ciphertext")]
    [InlineData("format")]
    [InlineData("version")]
    [InlineData("numeric-string")]
    [InlineData("extra")]
    [InlineData("profile")]
    [InlineData("object")]
    [InlineData("namespace")]
    [InlineData("actor-format")]
    [InlineData("actor-source")]
    [InlineData("actor-writer")]
    [InlineData("pending")]
    public void PreservesUnknownPlans(string fault)
    {
        using var fixture = new InstallerUninstallFixture();
        using var guard = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        _ = Prepare(fixture);
        var store = new InstallerDataStore(fixture.Product.Paths, "test");
        var actor = fault.StartsWith("actor-", StringComparison.Ordinal);
        var path = Path.Combine(store.DirectoryPath, actor ? "owner.protected" : fault == "pending" ? "owner.pending" : "intent.protected");
        if (fault == "pending") File.Copy(Path.Combine(store.DirectoryPath, "owner.protected"), path);
        if (fault == "ciphertext") File.WriteAllBytes(path, [3, 5, 7]);
        else if (fault == "namespace") File.WriteAllText(Path.Combine(store.DirectoryPath, "foreign.txt"), "Unknown retained bytes");
        else
        {
            var entropy = Encoding.UTF8.GetBytes("nexus.installer.data-removal/g0180/1");
            var plain = WindowsDataProtection.Transform(File.ReadAllBytes(path), entropy, protect: false);
            var document = JsonNode.Parse(plain)!;
            if (fault == "format") document["format"] = "other.data-removal";
            if (fault == "version") document["version"] = 2;
            if (fault == "numeric-string") document["version"] = "1";
            if (fault == "extra") document["foreign"] = true;
            if (fault == "profile") document["profile"] = "production";
            if (fault == "object") document["objects"]![0]!["identity"]!["fileId"] = new string('a', 32);
            if (fault == "actor-format") document["format"] = "other.data-actor";
            if (fault == "actor-source") document["intentSha256"] = new string('b', 64);
            if (fault == "actor-writer") document["writer"]!["imageSha256"] = new string('c', 64);
            if (fault == "pending") document["parentSha256"] = new string('d', 64);
            File.WriteAllBytes(path, WindowsDataProtection.Transform(JsonSerializer.SerializeToUtf8Bytes(document), entropy, protect: true));
        }
        var before = Snapshot(fixture.Instance.RuntimeRoot); var bytes = File.ReadAllBytes(path);
        var error = Record.Exception(() => fixture.Owner.DeleteData(fixture.Product.Application, Guid.NewGuid(), fixture.Native, fixture.WorkRoot));
        Assert.True(error is IOException or JsonException or System.ComponentModel.Win32Exception or CryptographicException);
        Assert.Equal(before, Snapshot(fixture.Instance.RuntimeRoot)); Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact(DisplayName = "A Known Interrupted Actor Publication Is Promoted Before Data Deletion Continues")]
    public void ResumesActorPublication()
    {
        using var fixture = new InstallerUninstallFixture();
        using var guard = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        _ = Prepare(fixture);
        var store = new InstallerDataStore(fixture.Product.Paths, "test");
        var path = Path.Combine(store.DirectoryPath, "owner.protected"); var bytes = File.ReadAllBytes(path);
        var transaction = Guid.NewGuid();
        var pending = InstallerDataCodec.Actor(bytes) with { TransactionId = transaction,
            ParentSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        File.WriteAllBytes(Path.Combine(store.DirectoryPath, "owner.pending"), InstallerDataCodec.Seal(pending));
        fixture.Owner.DeleteData(fixture.Product.Application, transaction, fixture.Native, fixture.WorkRoot);
        AssertRemoved(fixture);
        Assert.Equal(transaction, InstallerDataCodec.Actor(File.ReadAllBytes(path)).TransactionId);
        Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "owner.pending")));
    }

    [Fact(DisplayName = "Pending Explicit Deletion Blocks Host Recreation Even After Its Native Guard And Registration Are Gone")]
    public void BlocksRuntimeRecreation()
    {
        using var fixture = new InstallerUninstallFixture();
        using (InstallerRuntimeGate.Acquire(fixture.Product.Application))
            fixture.Owner.DeleteData(fixture.Product.Application, Guid.NewGuid(), fixture.Native, fixture.WorkRoot);
        Registry.CurrentUser.DeleteSubKeyTree(fixture.Product.Paths.RegistryKey);
        var previous = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("NEXUS_TEST_ROOT", fixture.Product.Root);
            Assert.Throws<IOException>(() => new InstanceRoot(new()).OpenOrCreate(fixture.Instance.RuntimeRoot));
            Assert.False(Directory.Exists(fixture.Instance.RuntimeRoot));
        }
        finally { Environment.SetEnvironmentVariable("NEXUS_TEST_ROOT", previous); }
    }

    [Fact(DisplayName = "Data Proof Is Cleared Only After The Actual Native Application Metadata And Shortcuts Have Been Removed")]
    public void RequiresNativeCompletion()
    {
        using var fixture = new InstallerUninstallFixture();
        using var guard = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        fixture.Owner.DeleteData(fixture.Product.Application, Guid.NewGuid(), fixture.Native, fixture.WorkRoot);
        Assert.Throws<IOException>(() => fixture.Owner.FinishData(fixture.Product.Application, Guid.NewGuid(), fixture.Native));
        Assert.True(new InstallerDataStore(fixture.Product.Paths, "test").Exists);
        Assert.True(File.Exists(Path.Combine(fixture.Product.Paths.ManagerRoot, InstallationAnchor.IdentityFile)));
        NativeRemoved(fixture);
        fixture.Owner.FinishData(fixture.Product.Application, Guid.NewGuid(), fixture.Native);
        Assert.False(Directory.Exists(fixture.Product.Paths.ManagerRoot));
    }

    [Theory(DisplayName = "Native Completion Preserves Unknown Installation And Recovery Metadata Before Removing Its Ownership Proof")]
    [InlineData("identity")]
    [InlineData("worker")]
    [InlineData("ownership")]
    [InlineData("manager")]
    [InlineData("namespace")]
    [InlineData("native-registry")]
    public void PreservesUnknownCompletion(string fault)
    {
        using var fixture = new InstallerUninstallFixture();
        using var guard = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        fixture.Owner.DeleteData(fixture.Product.Application, Guid.NewGuid(), fixture.Native, fixture.WorkRoot);
        NativeRemoved(fixture);
        var manager = fixture.Product.Paths.ManagerRoot;
        var seal = Path.Combine(manager, InstallationAnchor.IdentityFile);
        var worker = Path.Combine(manager, InstallationAnchor.WorkerFile);
        var intent = Path.Combine(manager, InstallerDataStore.DirectoryName, "intent.protected");
        if (fault == "identity") File.WriteAllText(seal, "Unknown identity bytes");
        if (fault == "worker") File.WriteAllText(worker, "Unknown worker bytes");
        if (fault == "ownership")
        {
            using var key = Registry.CurrentUser.OpenSubKey(fixture.Product.Paths.RegistryKey, writable: true);
            key!.SetValue("Foreign", "Unknown retained registry value");
        }
        if (fault == "manager") File.WriteAllText(Path.Combine(manager, "foreign.txt"), "Unknown retained manager bytes");
        if (fault == "namespace") File.WriteAllText(Path.Combine(manager, InstallerDataStore.DirectoryName, "foreign.txt"), "Unknown retained journal bytes");
        if (fault == "native-registry")
        {
            using var key = Registry.CurrentUser.CreateSubKey(fixture.Product.Paths.UninstallRegistryKey);
            key.SetValue("Foreign", "Unknown retained native registration");
        }
        var original = new[] { seal, worker, intent }.ToDictionary(path => path, File.ReadAllBytes);
        var ownership = JsonSerializer.Serialize(InstallerMetadataInventory.CaptureRegistry(fixture.Product.Paths.RegistryKey));
        Assert.Throws<IOException>(() => fixture.Owner.FinishData(fixture.Product.Application, Guid.NewGuid(), fixture.Native));
        foreach (var (path, bytes) in original) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(ownership, JsonSerializer.Serialize(InstallerMetadataInventory.CaptureRegistry(fixture.Product.Paths.RegistryKey)));
    }

    private static InstallerDataIntent Prepare(InstallerUninstallFixture fixture)
    {
        var frozen = InstallerRuntimeRemoval.Freeze(fixture.Identity);
        return new InstallerDataStore(fixture.Product.Paths, "test").Create(fixture.Product.Application, Guid.NewGuid(), fixture.Identity,
            InstallerMetadataInventory.Capture(fixture.Product.Application, fixture.Product.Paths, "ownership"), frozen.Tree, frozen.Objects, fixture.Native);
    }

    private static void Relocate(InstallerUninstallFixture fixture)
        => PhysicalObjectMove.MoveVerified(new(fixture.Instance.RuntimeRoot, fixture.Identity.RootIdentity, fixture.Identity.RuntimeIdentity,
            InstallerRuntimeRemoval.Retired(fixture.Product.Application), fixture.Identity.RootIdentity, FileObjectKind.Directory));

    private static void AssertRemoved(InstallerUninstallFixture fixture)
    {
        Assert.False(Directory.Exists(fixture.Instance.RuntimeRoot));
        Assert.False(Directory.Exists(InstallerRuntimeRemoval.Retired(fixture.Product.Application)));
        Assert.True(new InstallerDataStore(fixture.Product.Paths, "test").Exists);
    }

    private static void NativeRemoved(InstallerUninstallFixture fixture)
    {
        var root = Path.GetFullPath(fixture.Product.Application);
        Assert.StartsWith(Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!) + Path.DirectorySeparatorChar,
            root, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(@"D:\Projects\Temp\", root, StringComparison.OrdinalIgnoreCase);
        foreach (var name in ApplicationTree.Roots)
        {
            var path = Path.Combine(fixture.Product.Application, name);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true); else File.Delete(path);
        }
        Directory.Delete(Path.Combine(fixture.Product.Application, "uninstall"), recursive: true);
        Registry.CurrentUser.DeleteSubKeyTree(fixture.Product.Paths.UninstallRegistryKey);
    }

    private static string Snapshot(string root)
    {
        var snapshot = new FileTreeScanner(SnapshotBudget.Default).Capture(root, default);
        return JsonSerializer.Serialize(new { snapshot, objects = PhysicalTreeIdentity.Capture(root, snapshot) });
    }
}
