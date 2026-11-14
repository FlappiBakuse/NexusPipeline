using System.Runtime.Versioning;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerUninstallTests
{
    [Fact(DisplayName = "Installer Admission Reads An Actual Published Runtime Through The Complete Read Only Recovery Boundary")]
    public void AdmitsPublishedRuntime()
    {
        var assembly = Environment.GetEnvironmentVariable("NEXUS_PRODUCT_ASSEMBLY") ?? throw new IOException("product_assembly_required");
        var runtime = Environment.GetEnvironmentVariable("NEXUS_INSTALLER_RUNTIME") ?? Path.Combine(Path.GetDirectoryName(assembly)!, "runtime");
        var instance = new InstanceRoot(new()).OpenExisting(runtime);
        using var lease = InstanceLease.Acquire(instance, new());
        var files = new[] { "", "-wal", "-shm", "-journal" }.Select(suffix => Path.Combine(runtime, "state/runtime.db") + suffix).Where(File.Exists).ToArray();
        var before = files.Select(path => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        var work = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "installer-admission-" + Guid.NewGuid().ToString("N"));
        var root = Path.GetDirectoryName(runtime)!;
        var ready = new InstallerRuntimeAdmission(instance, new(), new(root, Path.Combine(runtime, "extensions"))).Open(lease, work);
        Assert.NotNull(ready);
        Assert.True(ready.Recovery.Current.IsReady);
        Assert.Equal(before, files.Select(path => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))).ToArray());
    }

    [Theory(DisplayName = "Default Uninstall Retains Exact Runtime Bytes And Physical Objects While Publishing The Retained Ownership State")]
    [InlineData(false)]
    [InlineData(true)]
    public void KeepsRuntimeData(bool initialized)
    {
        using var fixture = new InstallerUninstallFixture();
        if (initialized) { fixture.Initialize(); File.WriteAllText(Path.Combine(fixture.Instance.RuntimeRoot, "user-preservation.txt"), "Retained user bytes"); }
        var before = Snapshot(fixture.Instance.RuntimeRoot);
        var log = Path.Combine(fixture.Product.Application, "uninstall/unins000.dat");
        var nativeBytes = File.ReadAllBytes(log);
        using var nativeLog = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var gate = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        var retained = fixture.Owner.KeepData(fixture.Product.Application, Guid.NewGuid(), fixture.Native, fixture.WorkRoot);
        Assert.Equal(fixture.Identity with { State = "retained-data" }, retained);
        Assert.Equal(retained, new InstallationAnchor(fixture.Product.Paths.ManagerRoot, fixture.Product.Paths.RegistryKey).Read(fixture.Product.Application));
        Assert.Equal(before, Snapshot(fixture.Instance.RuntimeRoot));
        nativeLog.Position = 0;
        var retainedBytes = new byte[nativeBytes.Length]; nativeLog.ReadExactly(retainedBytes);
        Assert.Equal(nativeBytes, retainedBytes);
        Assert.False(Directory.Exists(fixture.Product.Checkpoint));
        Assert.Equal(retained, fixture.Owner.KeepData(fixture.Product.Application, Guid.NewGuid(), fixture.Native, fixture.WorkRoot));
        Assert.Equal(before, Snapshot(fixture.Instance.RuntimeRoot));
    }

    [Theory(DisplayName = "Uninstall Requires The Sealed Actual Native Parent Private Worker And Held Installer Guard Before Effects")]
    [InlineData("worker")]
    [InlineData("native")]
    [InlineData("guard")]
    public void RequiresNativeHandoff(string fault)
    {
        using var fixture = new InstallerUninstallFixture();
        var identity = fixture.Identity;
        if (fault == "worker") fixture.Save(identity with { Worker = identity.Worker with { Sha256 = new('a', 64) } });
        if (fault == "native") fixture.Save(identity with { NativeEngine = identity.NativeEngine with { Sha256 = new('b', 64) } });
        var before = File.ReadAllBytes(Path.Combine(fixture.Product.Paths.ManagerRoot, InstallationAnchor.IdentityFile));
        using var guard = fault == "guard" ? null : InstallerRuntimeGate.Acquire(fixture.Product.Application);
        Assert.Throws<IOException>(() => fixture.Owner.KeepData(fixture.Product.Application, Guid.NewGuid(), fixture.Native, fixture.WorkRoot));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(fixture.Product.Paths.ManagerRoot, InstallationAnchor.IdentityFile)));
        Assert.False(Directory.Exists(fixture.Product.Checkpoint));
    }

    [Fact(DisplayName = "Default Uninstall Rejects A Live Runtime Lease Before Metadata Or Runtime Changes")]
    public void RequiresStoppedRuntime()
    {
        using var fixture = new InstallerUninstallFixture();
        using var live = InstanceLease.Acquire(fixture.Instance, new());
        using var guard = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        var before = File.ReadAllBytes(Path.Combine(fixture.Product.Paths.ManagerRoot, InstallationAnchor.IdentityFile));
        Assert.Throws<IOException>(() => fixture.Owner.KeepData(fixture.Product.Application, Guid.NewGuid(), fixture.Native, fixture.WorkRoot));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(fixture.Product.Paths.ManagerRoot, InstallationAnchor.IdentityFile)));
        Assert.False(Directory.Exists(fixture.Product.Checkpoint));
    }

    [Theory(DisplayName = "Uninstall Admission Preserves Unknown Runtime Database Payload And Manager Inputs")]
    [InlineData("runtime")]
    [InlineData("database")]
    [InlineData("payload")]
    [InlineData("manager")]
    [InlineData("uninstall")]
    public void PreservesUnknownInputs(string fault)
    {
        using var fixture = new InstallerUninstallFixture();
        var file = Path.Combine(fixture.Instance.RuntimeRoot, "foreign.json");
        if (fault == "database") { fixture.Initialize(); file = Path.Combine(fixture.Instance.RuntimeRoot, "state/runtime.db"); }
        if (fault == "payload") file = Path.Combine(fixture.Product.Application, "README.md");
        if (fault == "manager") file = Path.Combine(fixture.Product.Paths.ManagerRoot, "foreign.txt");
        if (fault == "uninstall") file = Path.Combine(fixture.Product.Application, "uninstall/foreign.txt");
        File.WriteAllText(file, "Retained unknown source bytes");
        var bytes = File.ReadAllBytes(file); var physical = PhysicalObjectIdentity.Read(file);
        var identity = File.ReadAllBytes(Path.Combine(fixture.Product.Paths.ManagerRoot, InstallationAnchor.IdentityFile));
        using var guard = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        Assert.Throws<IOException>(() => fixture.Owner.KeepData(fixture.Product.Application, Guid.NewGuid(), fixture.Native, fixture.WorkRoot));
        Assert.Equal(bytes, File.ReadAllBytes(file)); Assert.Equal(physical, PhysicalObjectIdentity.Read(file));
        Assert.Equal(identity, File.ReadAllBytes(Path.Combine(fixture.Product.Paths.ManagerRoot, InstallationAnchor.IdentityFile)));
    }

    [Fact(DisplayName = "Uninstall Removes Owned Startup Through The Existing Durable Settings Intent Before Retaining User Data")]
    public void OrdersStartupRemoval()
    {
        using var fixture = new InstallerUninstallFixture(); var database = fixture.Initialize(autoStart: true);
        var saved = new HostSettingsStore(database).Read();
        fixture.Startup.BeforeMutation = () => Assert.False(new HostSettingsStore(database).Read().Values.AutoStart);
        using var guard = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        fixture.Owner.KeepData(fixture.Product.Application, Guid.NewGuid(), fixture.Native, fixture.WorkRoot);
        var after = new HostSettingsStore(database).Read();
        Assert.Equal(saved.Values with { AutoStart = false }, after.Values);
        Assert.Equal(saved.Revision + 1, after.Revision);
        Assert.Equal(1, fixture.Startup.Deletes); Assert.False(File.Exists(fixture.Startup.Path));
        Assert.False(Directory.Exists(fixture.Product.Checkpoint));
    }

    [Fact(DisplayName = "Unknown Startup Removal Stops Uninstall And Keeps The Original Installation Identity And Native Evidence")]
    public void PreservesStartupUnknown()
    {
        using var fixture = new InstallerUninstallFixture(); fixture.Initialize(autoStart: true);
        fixture.Startup.BeforeMutation = () => throw new IOException("controlled_outcome_unknown");
        var identity = File.ReadAllBytes(Path.Combine(fixture.Product.Paths.ManagerRoot, InstallationAnchor.IdentityFile));
        using var guard = InstallerRuntimeGate.Acquire(fixture.Product.Application);
        Assert.Throws<IOException>(() => fixture.Owner.KeepData(fixture.Product.Application, Guid.NewGuid(), fixture.Native, fixture.WorkRoot));
        Assert.Equal(identity, File.ReadAllBytes(Path.Combine(fixture.Product.Paths.ManagerRoot, InstallationAnchor.IdentityFile)));
        Assert.True(File.Exists(fixture.Startup.Path)); Assert.False(Directory.Exists(fixture.Product.Checkpoint));
    }

    private static string Snapshot(string root)
    {
        var tree = new FileTreeScanner(SnapshotBudget.Default).Capture(root, default);
        return System.Text.Json.JsonSerializer.Serialize(new { tree, objects = PhysicalTreeIdentity.Capture(root, tree) });
    }
}
