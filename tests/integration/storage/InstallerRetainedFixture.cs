using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class InstallerRetainedFixture : IDisposable
{
    internal InstallerRegistrationFixture Product { get; } = new();
    internal InstallationIdentity Identity { get; }
    internal RuntimeInstance Runtime { get; }
    internal Guid Transaction { get; } = Guid.NewGuid();
    internal string WorkRoot => Path.Combine(Product.Root, "inspection");
    internal string Marker => Path.Combine(Runtime.RuntimeRoot, "retained-user-data.bin");

    internal InstallerRetainedFixture(bool initialized = true)
    {
        Product.Begin(); Product.InstallPayload();
        var identity = Product.Owner.Register(Product.Application, Product.Transaction);
        Runtime = new InstanceRoot(new()).OpenExisting(Path.Combine(Product.Application, "runtime"));
        if (initialized)
        {
            using var lease = InstanceLease.Acquire(Runtime, new());
            var database = StateDatabase.CreateFresh(Runtime);
            _ = new StartupJournalRecovery(Runtime, new(), database,
                new(Product.Application, Path.Combine(Runtime.RuntimeRoot, "extensions"))).Recover(lease);
            File.WriteAllBytes(Marker, "Retained account and configuration fixture"u8.ToArray());
        }
        Identity = identity with { State = "retained-data" };
        var bytes = InstallationIdentityCodec.Seal(Identity);
        File.WriteAllBytes(Path.Combine(Product.Paths.ManagerRoot, InstallationAnchor.IdentityFile), bytes);
        using (var key = Registry.CurrentUser.OpenSubKey(Product.Paths.RegistryKey, writable: true))
            key!.SetValue("IdentitySha256", Convert.ToHexStringLower(SHA256.HashData(bytes)), RegistryValueKind.String);
        foreach (var name in ApplicationTree.Roots)
        {
            var source = Path.Combine(Product.Application, name);
            var destination = Path.Combine(Product.Candidate, name);
            if (Directory.Exists(source)) Directory.Move(source, destination); else File.Move(source, destination);
        }
        Directory.Delete(Path.Combine(Product.Application, "uninstall"), recursive: true);
        if (initialized)
        {
            Directory.CreateDirectory(Path.Combine(Product.Application, "uninstall"));
        }
        Registry.CurrentUser.DeleteSubKeyTree(Product.Paths.UninstallRegistryKey);
    }

    internal void Begin(string? workRoot = null)
        => Product.Owner.Begin(Product.Application, Transaction, Product.Target, Parent(), workRoot ?? WorkRoot);

    internal static ProcessIdentity Parent()
    {
        var processes = ProcessSnapshotProbe.Capture(Environment.ProcessId, "", default);
        if (processes.Truncated || processes.Processes.Count != 1) throw new IOException("fixture_parent_unknown");
        return ProcessIdentityReader.Read(processes.Processes[0].Ppid);
    }

    public void Dispose() => Product.Dispose();
}
