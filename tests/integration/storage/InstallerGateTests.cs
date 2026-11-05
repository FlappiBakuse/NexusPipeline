using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerGateTests
{
    [Fact(DisplayName = "A Native Installer Presence Guard Rejects Runtime Creation Until Its Last Handle Closes")]
    public void BlocksRuntimeCreation()
    {
        var root = Root(); var runtime = Path.Combine(root, "runtime");
        using (InstallerRuntimeGate.Acquire(root))
        {
            Assert.Throws<IOException>(() => new InstanceRoot(new()).OpenOrCreate(runtime));
            Assert.Throws<IOException>(() => InstallerRuntimeGate.Acquire(root));
            Assert.False(Directory.Exists(runtime));
        }
        Assert.NotEqual(Guid.Empty, new InstanceRoot(new()).OpenOrCreate(runtime).InstanceId);
    }

    [Fact(DisplayName = "Existing Runtime Admission Respects Native Setup While Its Explicit Actual Child Retains File Exclusivity")]
    public void SeparatesInstallerAdmission()
    {
        var root = Root(); var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
        using (InstallerRuntimeGate.Acquire(root))
        {
            Assert.Throws<IOException>(() => InstanceLease.Acquire(instance, new()));
            using var installer = InstanceLease.AcquireInstaller(instance, new(), Parent());
            Assert.Equal(instance, installer.Instance);
            Assert.Throws<IOException>(() => InstanceLease.AcquireInstaller(instance, new(), Parent()));
        }
        using var available = InstanceLease.Acquire(instance, new());
        Assert.Equal(instance, available.Instance);
    }

    [Theory(DisplayName = "An Installer Runtime Bypass Requires The Exact Actual Parent Process Identity")]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsForeignParent(bool creation)
    {
        var root = Root(); var runtime = Path.Combine(root, "runtime");
        var parent = Parent(); parent = creation ? parent with { CreatedAtUtc = parent.CreatedAtUtc.AddSeconds(-1) } : parent with { ProcessId = int.MaxValue };
        using var guard = InstallerRuntimeGate.Acquire(root);
        Assert.Throws<IOException>(() => new InstanceRoot(new()).OpenInstaller(runtime, parent));
        Assert.False(Directory.Exists(runtime));
    }

    [Fact(DisplayName = "A Retained Installation Cannot Reopen Runtime Data Through The Normal Host Admission")]
    public void BlocksRetainedRuntime()
    {
        using var fixture = new InstallerIdentityFixture(installerContext: true);
        var saved = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("NEXUS_TEST_ROOT", fixture.Root);
            fixture.Save(InstallationIdentityCodec.Seal(fixture.Identity with { State = "retained-data" }));
            var path = Path.Combine(fixture.Application, "runtime", "instance.json"); var bytes = File.ReadAllBytes(path);
            Assert.Throws<IOException>(() => new InstanceRoot(new()).OpenOrCreate(Path.GetDirectoryName(path)!));
            Assert.Equal(bytes, File.ReadAllBytes(path));
            fixture.Save(InstallationIdentityCodec.Seal(fixture.Identity));
            Assert.Equal(fixture.Identity.RuntimeId, new InstanceRoot(new()).OpenOrCreate(Path.GetDirectoryName(path)!).InstanceId);
        }
        finally { Environment.SetEnvironmentVariable("NEXUS_TEST_ROOT", saved); }
    }

    [Fact(DisplayName = "A Durable Pending Installer Checkpoint Blocks Normal Host Initialization After The Native Guard Closes")]
    public void BlocksPendingCheckpoint()
    {
        var root = Root(); var scope = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(root, ".nexus-installer-fixture"), scope);
        var saved = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("NEXUS_TEST_ROOT", root);
            var paths = InstallerPaths.Current();
            var application = Path.Combine(root, "application"); var id = Guid.NewGuid();
            Directory.CreateDirectory(application);
            var store = new InstallerCheckpointStore(paths, "test");
            store.Create(application, id, new('a', 64), Parent());
            Assert.Throws<IOException>(() => new InstanceRoot(new()).OpenOrCreate(Path.Combine(application, "runtime")));
            Assert.False(Directory.Exists(Path.Combine(application, "runtime")));
            new InstallerMetadataRecovery(paths, store).RestoreCurrentMetadata(application, id);
            new InstallerCheckpointCompletion(paths, store).CompleteCurrent(application, id, committed: false);
            Assert.NotEqual(Guid.Empty, new InstanceRoot(new()).OpenOrCreate(Path.Combine(application, "runtime")).InstanceId);
        }
        finally { Environment.SetEnvironmentVariable("NEXUS_TEST_ROOT", saved); }
    }

    private static string Root()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "installer-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }

    private static ProcessIdentity Parent()
    {
        var processes = ProcessSnapshotProbe.Capture(Environment.ProcessId, "", default);
        Assert.False(processes.Truncated);
        return ProcessIdentityReader.Read(Assert.Single(processes.Processes).Ppid);
    }
}
