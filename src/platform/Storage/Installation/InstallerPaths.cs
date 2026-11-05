using System.Runtime.Versioning;
using Microsoft.Win32;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal sealed record InstallerPaths(string ManagerRoot, string RegistryKey)
{
    internal string UninstallRegistryKey => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" +
        (RegistryKey == InstallationAnchor.RegistryPath ? "NexusPipeline.PerUser.0.18" : "NexusPipeline.Tests." + TestScope()) + "_is1";

    internal string DesktopRoot => RegistryKey == InstallationAnchor.RegistryPath
        ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) : Path.Combine(Path.GetDirectoryName(ManagerRoot)!, "desktop");

    internal string GroupRoot => RegistryKey == InstallationAnchor.RegistryPath
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "NexusPipeline")
        : Path.Combine(Path.GetDirectoryName(ManagerRoot)!, "programs", "NexusPipeline");

    private string TestScope()
    {
        const string prefix = @"Software\NexusPipeline\Tests\Installer\";
        if (!RegistryKey.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(RegistryKey[prefix.Length..], "N", out _))
            throw InstallationIdentityCodec.Invalid();
        return RegistryKey[prefix.Length..];
    }

    internal static InstallerPaths Current()
    {
#if NEXUS_TEST_HOST
        return InstallerTestPaths.Current();
#else
        return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NexusPipeline.Generations", "g0180", "installer"), InstallationAnchor.RegistryPath);
#endif
    }

    internal static IEnumerable<string> ProtectedRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Programs", "NexusPipeline");
        yield return Path.Combine(local, "NexusPipeline.Generations", "g01615");
        foreach (var path in new[] { @"Software\NexusPipeline\Installer",
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NexusPipeline.PerUser_is1",
            @"Software\NexusPipeline.Generations\g01615\Installer",
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NexusPipeline.PerUser.g01615_is1" })
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            foreach (var name in new[] { "AppDir", "DataRoot", "InstallLocation" })
            {
                var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value is null) continue;
                if (value is not string root || string.IsNullOrWhiteSpace(root)) throw InstallationIdentityCodec.Invalid();
                yield return root;
            }
        }
    }
}
