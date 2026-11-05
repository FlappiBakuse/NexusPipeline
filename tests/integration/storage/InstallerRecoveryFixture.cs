using System.Runtime.Versioning;
using Microsoft.Win32;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Installation;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class InstallerRecoveryFixture : IDisposable
{
    internal InstallerMetadataFixture Metadata { get; }
    internal InstallerCheckpointStore Store { get; }
    internal InstallerMetadataBackup Backup { get; }
    internal InstallerMetadataRecovery Recovery { get; }
    internal InstallerCheckpoint Prepared { get; }
    internal InstallerCheckpoint? Observed { get; private set; }
    internal InstallerMetadataSnapshot Before => InstallerSnapshotCodec.Decode(Prepared.Before, Metadata.Application);
    internal Guid Transaction => Prepared.TransactionId;

    internal InstallerRecoveryFixture(bool fresh = false, bool typedValues = false)
    {
        Metadata = new(seed: !fresh);
        Directory.CreateDirectory(Metadata.Application);
        if (typedValues) WriteValues(original: true);
        Store = new(Metadata.Paths, "test"); Backup = new(Metadata.Paths, Store); Recovery = new(Metadata.Paths, Store);
        var processes = ProcessSnapshotProbe.Capture(Environment.ProcessId, "", default);
        if (processes.Truncated || processes.Processes.Count != 1) throw new IOException("installer_parent_fixture_unavailable");
        Prepared = Store.Create(Metadata.Application, Guid.NewGuid(), new('a', 64), ProcessIdentityReader.Read(processes.Processes[0].Ppid));
    }

    internal void InstallMetadata(bool typedValues = false)
    {
        Backup.Preposition(Metadata.Application, Transaction);
        Directory.CreateDirectory(Path.GetDirectoryName(Metadata.Engine)!);
        File.WriteAllText(Metadata.Engine, "New controlled native engine");
        File.WriteAllText(Path.ChangeExtension(Metadata.Engine, "dat"), "New controlled native state");
        File.WriteAllText(Path.Combine(Metadata.Paths.ManagerRoot, InstallationAnchor.IdentityFile), "New controlled seal");
        File.WriteAllText(Path.Combine(Metadata.Paths.ManagerRoot, InstallationAnchor.WorkerFile), "New controlled worker");
        Metadata.WriteShortcut(Path.Combine(Metadata.Application, "NexusPipeline.exe"));
        using (var key = Registry.CurrentUser.CreateSubKey(Metadata.Paths.UninstallRegistryKey))
        {
            key.SetValue("InstallLocation", Metadata.Application); key.SetValue("UninstallString", '"' + Metadata.Engine + '"');
            key.SetValue("DisplayVersion", "0.18.0"); key.SetValue("DisplayName", "New controlled registration");
        }
        if (typedValues) WriteValues(original: false);
        else { using var key = Registry.CurrentUser.CreateSubKey(Metadata.Paths.RegistryKey); key.SetValue("ControlledOwner", "target"); }
        Observed = Store.Observe(Metadata.Application, Transaction);
    }

    private void WriteValues(bool original)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Metadata.Paths.RegistryKey);
        key.SetValue("String", original ? "Original text" : "Target text", RegistryValueKind.String);
        key.SetValue("Expand", original ? "%LOCALAPPDATA%\\original" : "%TEMP%\\target", RegistryValueKind.ExpandString);
        key.SetValue("Lines", original ? new[] { "one", "two" } : ["three"], RegistryValueKind.MultiString);
        key.SetValue("Binary", original ? new byte[] { 1, 2 } : [3, 4, 5], RegistryValueKind.Binary);
        key.SetValue("Dword", original ? -1 : 42, RegistryValueKind.DWord);
        key.SetValue("Qword", original ? long.MaxValue : 99L, RegistryValueKind.QWord);
    }

    public void Dispose() => Metadata.Dispose();
}
