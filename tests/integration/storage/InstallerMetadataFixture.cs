using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Installation;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class InstallerMetadataFixture : IDisposable
{
    internal string Root { get; }
    internal string Application { get; }
    internal InstallerPaths Paths { get; }
    internal string Engine => Path.Combine(Application, "uninstall", "unins000.exe");
    internal string Shortcut => Path.Combine(Paths.DesktopRoot, "NexusPipeline v0.18.0.lnk");

    internal OriginalMetadata[] ReadOriginalMetadata(bool includeShortcut = false)
    {
        var files = new List<(string Role, string Source)>
        {
            ("manager", Path.Combine(Paths.ManagerRoot, "identity.protected")),
            ("manager", Path.Combine(Paths.ManagerRoot, "nexus-installer-worker.exe")),
            ("uninstall", Path.ChangeExtension(Engine, "dat")),
            ("uninstall", Engine)
        };
        if (includeShortcut) files.Insert(0, ("desktop", Shortcut));
        return files.Select(file => new OriginalMetadata(file.Role, file.Source, File.ReadAllBytes(file.Source),
            PhysicalObjectIdentity.Read(file.Source), PhysicalObjectIdentity.Read(Path.GetDirectoryName(file.Source)!))).ToArray();
    }

    internal sealed record OriginalMetadata(string Role, string Source, byte[] Bytes, PhysicalObjectIdentity Identity,
        PhysicalObjectIdentity ParentIdentity)
    {
        internal string Name => Path.GetFileName(Source);
        internal string BackupName => $"{Role}--{Name}";
    }

    internal InstallerMetadataFixture(bool seed = true)
    {
        var scope = Guid.NewGuid().ToString("N");
        Root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "installer-metadata-" + scope);
        Application = Path.Combine(Root, "application");
        Paths = new(Path.Combine(Root, "manager"), @"Software\NexusPipeline\Tests\Installer\" + scope);
        if (!seed) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Engine)!); Directory.CreateDirectory(Paths.ManagerRoot);
        File.WriteAllBytes(Engine, "Controlled native installer engine bytes"u8.ToArray());
        File.WriteAllBytes(Path.ChangeExtension(Engine, "dat"), "Controlled native installer state"u8.ToArray());
        File.WriteAllBytes(Path.Combine(Paths.ManagerRoot, InstallationAnchor.IdentityFile), "Controlled opaque seal bytes"u8.ToArray());
        File.WriteAllBytes(Path.Combine(Paths.ManagerRoot, InstallationAnchor.WorkerFile), "Controlled worker bytes"u8.ToArray());
        using var key = Registry.CurrentUser.CreateSubKey(Paths.UninstallRegistryKey, writable: true);
        key.SetValue("InstallLocation", Application, RegistryValueKind.String);
        key.SetValue("UninstallString", '"' + Engine + '"', RegistryValueKind.String);
    }

    internal void WriteShortcut(string target, string arguments = "")
    {
        Directory.CreateDirectory(Paths.DesktopRoot);
        object? shell = null; object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            dynamic link = shortcut = ((dynamic)shell!).CreateShortcut(Shortcut);
            link.TargetPath = target; link.Arguments = arguments; link.Save();
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(Paths.RegistryKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(Paths.UninstallRegistryKey, throwOnMissingSubKey: false);
    }
}
