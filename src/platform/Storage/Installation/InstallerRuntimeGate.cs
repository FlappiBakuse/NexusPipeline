using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal sealed class InstallerRuntimeGate : IDisposable
{
    private readonly Mutex _handle;
    private InstallerRuntimeGate(Mutex handle) => _handle = handle;

    internal static string Name(string applicationRoot)
    {
        var root = PhysicalPathPolicy.NormalizeWithoutLinks(applicationRoot);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
        return @"Global\NexusPipeline.Installer." + InstallationIdentityCodec.UserSid + "." + digest;
    }

    internal static InstallerRuntimeGate Acquire(string applicationRoot)
    {
        var handle = new Mutex(initiallyOwned: false, Name(applicationRoot), out var created);
        if (created) return new(handle);
        handle.Dispose(); throw new IOException("installer_runtime_guard_owned");
    }

    internal static void RequireHeld(string applicationRoot)
    {
        if (!Mutex.TryOpenExisting(Name(applicationRoot), out var held)) throw new IOException("installer_runtime_guard_missing");
        held.Dispose();
    }

    internal static void RequireAvailable(string runtimeRoot)
    {
        var parent = Path.GetDirectoryName(PhysicalPathPolicy.NormalizeWithoutLinks(runtimeRoot)) ?? throw InstallationIdentityCodec.Invalid();
        foreach (var root in new[] { parent, PhysicalPathPolicy.RuntimeApplicationDirectory() }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Mutex.TryOpenExisting(Name(root), out var held))
            {
                held.Dispose(); throw new IOException("installer_runtime_guard_owned");
            }
            RequireActive(root);
        }
    }

    private static void RequireActive(string root)
    {
#if NEXUS_TEST_HOST
        var paths = InstallerTestPaths.TryCurrent();
        if (paths is null) return;
#else
        var paths = InstallerPaths.Current();
#endif
        var removal = Path.Combine(paths.ManagerRoot, InstallerDataStore.DirectoryName);
        PhysicalPathPolicy.NormalizeWithoutLinks(removal);
        if (File.Exists(removal)) throw InstallationIdentityCodec.Invalid();
        if (Directory.Exists(removal) && StringComparer.OrdinalIgnoreCase.Equals(root, InstallerDataStore.ReadPending(paths).Root))
            throw new IOException("installer_data_removal_pending");
        using var key = Registry.CurrentUser.OpenSubKey(paths.RegistryKey, writable: false);
        if (key?.GetValue("ApplicationRoot", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string registered &&
            StringComparer.OrdinalIgnoreCase.Equals(root, registered))
        {
            var identity = new InstallationAnchor(paths.ManagerRoot, paths.RegistryKey).Read(root) ?? throw InstallationIdentityCodec.Invalid();
            if (identity.State != "active") throw new IOException("installer_runtime_retained");
        }
        var checkpointRoot = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(paths.ManagerRoot, "metadata-checkpoint"));
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(checkpointRoot,
            File.Exists(Path.Combine(checkpointRoot, "committed.protected")) ? "committed.protected" : "prepared.protected"));
        if (!File.Exists(path)) return;
        PhysicalObjectIdentity.RequireSingleLink(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > InstallerCheckpointCodec.MaximumProtectedBytes) throw InstallationIdentityCodec.Invalid();
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        var checkpoint = InstallerCheckpointCodec.Unseal(bytes);
        if (StringComparer.OrdinalIgnoreCase.Equals(root, checkpoint.Root)) throw new IOException("installer_runtime_recovery_pending");
    }

    public void Dispose() => _handle.Dispose();
}
