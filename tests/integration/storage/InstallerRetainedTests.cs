using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerRetainedTests
{
    [Theory(DisplayName = "Retained Reinstall Preserves Runtime Bytes Physical Objects Installation Identity And Creation Time")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReinstallsRetainedRuntime(bool initialized)
    {
        using var fixture = new InstallerRetainedFixture(initialized);
        var product = fixture.Product;
        var before = Files(fixture.Runtime.RuntimeRoot);
        using var guard = InstallerRuntimeGate.Acquire(product.Application);
        fixture.Begin(); fixture.Begin(); product.InstallPayload();
        var installed = product.Owner.Register(product.Application, fixture.Transaction);
        Assert.Equal(fixture.Identity.InstallationId, installed.InstallationId);
        Assert.Equal(fixture.Identity.CreatedAt, installed.CreatedAt);
        Assert.Equal(fixture.Identity.RuntimeId, installed.RuntimeId);
        Assert.Equal(fixture.Identity.RuntimeIdentity, installed.RuntimeIdentity);
        Assert.Equal("active", installed.State);
        Assert.Equal(before, Files(fixture.Runtime.RuntimeRoot));
        Assert.Equal(installed, product.Owner.Register(product.Application, fixture.Transaction));
        Assert.False(Directory.Exists(product.Checkpoint));
        Assert.Equal(installed, new InstallationAnchor(product.Paths.ManagerRoot, product.Paths.RegistryKey).Read(product.Application));
    }

    [Fact(DisplayName = "Cancelling A Prepared Retained Reinstall Restores Original Metadata And Keeps Runtime Bytes")]
    public void CancelsRetainedPreparation()
    {
        using var fixture = new InstallerRetainedFixture();
        var product = fixture.Product;
        var before = InstallerMetadataInventory.Capture(product.Application, product.Paths);
        var runtime = Files(fixture.Runtime.RuntimeRoot);
        using var guard = InstallerRuntimeGate.Acquire(product.Application);
        fixture.Begin(); product.Owner.CancelPrepared(product.Application, fixture.Transaction);
        Assert.True(InstallerMetadataInventory.Equal(before, InstallerMetadataInventory.Capture(product.Application, product.Paths)));
        Assert.Equal(runtime, Files(fixture.Runtime.RuntimeRoot));
        Assert.Equal(fixture.Identity, new InstallationAnchor(product.Paths.ManagerRoot, product.Paths.RegistryKey).Read(product.Application));
        Assert.False(Directory.Exists(product.Checkpoint));
    }

    [Theory(DisplayName = "Retained Reinstall Rejects Unsafe Inputs Before Preparing Native Metadata Or Changing User Bytes")]
    [InlineData("guard")]
    [InlineData("lease")]
    [InlineData("database")]
    [InlineData("manager")]
    [InlineData("ownership")]
    [InlineData("application")]
    [InlineData("native")]
    [InlineData("bundled")]
    [InlineData("inspection")]
    public void PreservesUnsafeRetained(string fault)
    {
        using var fixture = new InstallerRetainedFixture();
        var product = fixture.Product;
        var bundled = Path.Combine(product.Application, "plugins/EmulatorSupport/index.json");
        if (fault == "bundled")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
            File.WriteAllText(bundled, "Retained public bundle data");
        }
        if (fault == "database") File.WriteAllText(Path.Combine(fixture.Runtime.RuntimeRoot, "state/runtime.db"), "Retained unknown database");
        if (fault == "manager") File.WriteAllText(Path.Combine(product.Paths.ManagerRoot, "foreign.txt"), "Retained unknown manager");
        if (fault == "ownership")
        {
            using var key = Registry.CurrentUser.OpenSubKey(product.Paths.RegistryKey, writable: true);
            key!.SetValue("Unknown", "Retained foreign value");
        }
        if (fault == "application") File.WriteAllText(Path.Combine(product.Application, ApplicationTree.HostEntry), "Retained application");
        if (fault == "native")
        {
            using var key = Registry.CurrentUser.CreateSubKey(product.Paths.UninstallRegistryKey);
            key.SetValue("Foreign", "Retained native registration");
        }
        using var lease = fault == "lease" ? InstanceLease.Acquire(fixture.Runtime, new()) : null;
        using var guard = fault == "guard" ? null : InstallerRuntimeGate.Acquire(product.Application);
        var runtime = File.ReadAllBytes(fixture.Marker);
        var identity = File.ReadAllBytes(Path.Combine(product.Paths.ManagerRoot, InstallationAnchor.IdentityFile));
        var bundledIdentity = fault == "bundled" ? PhysicalObjectIdentity.Read(bundled) : null;
        Assert.ThrowsAny<IOException>(() => product.Owner.Begin(product.Application, fixture.Transaction, product.Target,
            InstallerRetainedFixture.Parent(), fault == "inspection" ? null : fixture.WorkRoot));
        Assert.Equal(runtime, File.ReadAllBytes(fixture.Marker));
        Assert.Equal(identity, File.ReadAllBytes(Path.Combine(product.Paths.ManagerRoot, InstallationAnchor.IdentityFile)));
        Assert.False(Directory.Exists(product.Checkpoint));
        if (fault == "bundled")
        {
            Assert.Equal("Retained public bundle data", File.ReadAllText(bundled));
            Assert.Equal(bundledIdentity, PhysicalObjectIdentity.Read(bundled));
        }
    }

    [Theory(DisplayName = "Retained Registration Resumes Only Recorded Identity Worker And Registry Publication Prefixes")]
    [InlineData(false)]
    [InlineData(true)]
    public void ResumesRetainedPublication(bool registryPrefix)
    {
        using var fixture = new InstallerRetainedFixture(); var product = fixture.Product;
        var runtime = Files(fixture.Runtime.RuntimeRoot);
        using var guard = InstallerRuntimeGate.Acquire(product.Application);
        fixture.Begin(); product.InstallPayload(); product.Owner.PrepareRegistration(product.Application, fixture.Transaction);
        var store = new InstallerCheckpointStore(product.Paths, "test");
        var frame = store.Read(product.Application, fixture.Transaction);
        var backup = new InstallerMetadataBackup(product.Paths, store);
        var before = InstallerSnapshotCodec.Decode(frame.Before, product.Application);
        var original = before.Files.Single(file => file.Name == InstallationAnchor.IdentityFile);
        File.Move(Path.Combine(product.Paths.ManagerRoot, original.Name), backup.BackupPath(original));
        File.Move(Path.Combine(product.Checkpoint, "identity.pending"), Path.Combine(product.Paths.ManagerRoot, original.Name));
        if (registryPrefix)
        {
            var target = InstallerSnapshotCodec.Decode(frame.After!, product.Application);
            InstallerRegistryEffects.Restore(product.Paths.RegistryKey, target.Ownership, before.Ownership);
        }
        var installed = product.Owner.Register(product.Application, fixture.Transaction);
        Assert.Equal(fixture.Identity.InstallationId, installed.InstallationId);
        Assert.Equal(fixture.Identity.RuntimeId, installed.RuntimeId);
        Assert.Equal(runtime, Files(fixture.Runtime.RuntimeRoot));
        Assert.False(Directory.Exists(product.Checkpoint));
    }

    [Theory(DisplayName = "Retained Registration Preserves Changed Ownership Runtime And Worker Plans")]
    [InlineData("ownership")]
    [InlineData("runtime")]
    [InlineData("worker")]
    public void PreservesRetainedChanges(string fault)
    {
        using var fixture = new InstallerRetainedFixture(); var product = fixture.Product;
        using var guard = InstallerRuntimeGate.Acquire(product.Application);
        fixture.Begin(); product.InstallPayload();
        if (fault == "ownership")
        {
            using var key = Registry.CurrentUser.OpenSubKey(product.Paths.RegistryKey, writable: true);
            key!.SetValue("IdentitySha256", new string('b', 64));
        }
        if (fault == "runtime") File.WriteAllText(Path.Combine(fixture.Runtime.RuntimeRoot, "instance.json"), "Retained unknown identity");
        if (fault == "worker")
        {
            product.Owner.PrepareRegistration(product.Application, fixture.Transaction);
            File.WriteAllText(Path.Combine(product.Checkpoint, "worker.pending"), "Retained unknown worker");
        }
        var marker = File.ReadAllBytes(fixture.Marker);
        var identity = File.ReadAllBytes(Path.Combine(product.Paths.ManagerRoot, InstallationAnchor.IdentityFile));
        var error = Record.Exception(() => product.Owner.Register(product.Application, fixture.Transaction));
        Assert.True(error is IOException or JsonException);
        Assert.Equal(marker, File.ReadAllBytes(fixture.Marker));
        Assert.Equal(identity, File.ReadAllBytes(Path.Combine(product.Paths.ManagerRoot, InstallationAnchor.IdentityFile)));
        Assert.True(Directory.Exists(product.Checkpoint));
    }

    [Fact(DisplayName = "The Published Installer Worker Reinstalls Its Retained Runtime Through Actual Parent Bound Commands")]
    public async Task ReinstallsThroughEntry()
    {
        using var fixture = new InstallerRetainedFixture(); var product = fixture.Product;
        var before = Files(fixture.Runtime.RuntimeRoot);
        Assert.Equal(10, (await product.Execute("installer-state", "check-new", "--root", product.Application)).ExitCode);
        Assert.Equal(0, (await product.Execute("installer-state", "check-install", "--root", product.Application)).ExitCode);
        using var guard = InstallerRuntimeGate.Acquire(product.Application);
        var arguments = new[] { "installer-state", "begin", "--root", product.Application, "--transaction", fixture.Transaction.ToString("D"),
            "--manifest", product.Digest, "--manifest-file", product.TargetPath, "--work-root", fixture.WorkRoot };
        Assert.Equal(0, (await product.Execute(arguments)).ExitCode);
        Assert.Equal(0, (await product.Execute(arguments)).ExitCode);
        product.InstallPayload();
        var registered = await product.Execute("installer-state", "register", "--root", product.Application,
            "--transaction", fixture.Transaction.ToString("D"));
        Assert.Equal(0, registered.ExitCode);
        using var document = JsonDocument.Parse(registered.Output);
        Assert.Equal(fixture.Identity.InstallationId, document.RootElement.GetProperty("installationId").GetGuid());
        Assert.Equal(fixture.Runtime.InstanceId, document.RootElement.GetProperty("runtimeId").GetGuid());
        Assert.Equal(before, Files(fixture.Runtime.RuntimeRoot));
        Assert.False(Directory.Exists(product.Checkpoint));
    }

    [Fact(DisplayName = "The Published Installer Worker Cancels Retained Preparation With Exact Original Metadata And Data")]
    public async Task CancelsThroughEntry()
    {
        using var fixture = new InstallerRetainedFixture(); var product = fixture.Product;
        var before = InstallerMetadataInventory.Capture(product.Application, product.Paths);
        var runtime = Files(fixture.Runtime.RuntimeRoot);
        using var guard = InstallerRuntimeGate.Acquire(product.Application);
        Assert.Equal(0, (await product.Execute("installer-state", "begin", "--root", product.Application,
            "--transaction", fixture.Transaction.ToString("D"), "--manifest", product.Digest, "--manifest-file", product.TargetPath,
            "--work-root", fixture.WorkRoot)).ExitCode);
        Assert.Equal(0, (await product.Execute("installer-state", "cancel-prepared", "--root", product.Application,
            "--transaction", fixture.Transaction.ToString("D"))).ExitCode);
        Assert.True(InstallerMetadataInventory.Equal(before, InstallerMetadataInventory.Capture(product.Application, product.Paths)));
        Assert.Equal(runtime, Files(fixture.Runtime.RuntimeRoot));
        Assert.False(Directory.Exists(product.Checkpoint));
    }

    private static (string Name, PhysicalObjectIdentity Identity, string Bytes)[] Files(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => (Path.GetRelativePath(root, path), PhysicalObjectIdentity.Read(path), Convert.ToBase64String(File.ReadAllBytes(path)))).ToArray();
}
