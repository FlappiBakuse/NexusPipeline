using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Settings;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Testing;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class InstallerUninstallFixture : IDisposable
{
    internal InstallerRegistrationFixture Product { get; } = new();
    internal InstallationIdentity Identity { get; private set; }
    internal RuntimeInstance Instance { get; }
    internal ProcessIdentity Native { get; }
    internal ControlledStartupRegistration Startup { get; }
    internal InstallerUninstallation Owner { get; }
    internal string WorkRoot => Path.Combine(Product.Root, "inspection");

    internal InstallerUninstallFixture()
    {
        Product.Begin(); Product.InstallPayload();
        var identity = Product.Owner.Register(Product.Application, Product.Transaction);
        var processes = ProcessSnapshotProbe.Capture(Environment.ProcessId, "", default);
        if (processes.Truncated || processes.Processes.Count != 1) throw new IOException("fixture_parent_unknown");
        Native = ProcessIdentityReader.Read(processes.Processes[0].Ppid);
        var self = ProcessIdentityReader.Read(Environment.ProcessId);
        var worker = Path.Combine(Product.Paths.ManagerRoot, InstallationAnchor.WorkerFile);
        var engine = Path.Combine(Product.Application, InstallationAnchor.EngineEntry);
        File.Copy(self.ImagePath, worker, overwrite: true); File.Copy(Native.ImagePath, engine, overwrite: true);
        Identity = identity with { Worker = Proof(worker), NativeEngine = Proof(engine) };
        Save(Identity);
        Instance = new InstanceRoot(new()).OpenExisting(Path.Combine(Product.Application, "runtime"));
        Startup = new(Path.Combine(Product.Root, "controlled-task.xml"));
        Owner = new(Product.Paths, "test", Startup);
    }

    internal StateDatabase Initialize(bool autoStart = false)
    {
        using var lease = InstanceLease.Acquire(Instance, new());
        var database = StateDatabase.CreateFresh(Instance);
        var index = new StartupJournalRecovery(Instance, new(), database, new(Product.Application, Path.Combine(Instance.RuntimeRoot, "extensions"))).Recover(lease);
        if (autoStart)
        {
            var resources = new ResourceLeaseManager(new HostLifecycle(index, TimeProvider.System));
            var settings = HostStartupSettings.CreateForInstallation(Instance, database, lease, resources, index,
                Path.Combine(Product.Application, ApplicationTree.HostEntry), Startup);
            var current = new HostSettingsStore(database).Read();
            var change = new HostSettingsChange(current.Revision, current.Values with { AutoStart = true });
            settings.Save(HostSettingsStore.Identity(Guid.NewGuid(), "fixture", change), change, DateTimeOffset.UtcNow);
        }
        return database;
    }

    internal void Save(InstallationIdentity identity)
    {
        var bytes = InstallationIdentityCodec.Seal(identity);
        File.WriteAllBytes(Path.Combine(Product.Paths.ManagerRoot, InstallationAnchor.IdentityFile), bytes);
        using var key = Registry.CurrentUser.OpenSubKey(Product.Paths.RegistryKey, writable: true);
        key!.SetValue("IdentitySha256", Convert.ToHexStringLower(SHA256.HashData(bytes)), RegistryValueKind.String);
    }

    private static InstallerFile Proof(string path) => new(new FileInfo(path).Length,
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), PhysicalObjectIdentity.Read(path));

    public void Dispose() => Product.Dispose();
}
