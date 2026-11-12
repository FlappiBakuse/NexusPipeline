using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Settings;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Windows;

namespace NexusPipeline.Platform.Storage.Settings;

[SupportedOSPlatform("windows")]
public static class HostStartupSettings
{
    public static HostSettingsService Create(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceLeaseManager resources, RecoveryIndex recovery)
    {
        var host = PhysicalPathPolicy.RuntimeProcessPath();
        var executable = host;
        var imageSha256 = lease.ExecutorIdentity.ReadCurrent().ImageSha256;
        var manifestPath = Path.Combine(Path.GetDirectoryName(host)!, ApplicationTree.ManifestPath);
        if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(host), ApplicationTree.HostEntry) && File.Exists(manifestPath))
        {
            manifestPath = PhysicalPathPolicy.NormalizeWithoutLinks(manifestPath);
            using var manifest = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (manifest.Length is < 1 or > StrictJson.MaximumBytes) throw ApplicationTree.Changed();
            var bytes = new byte[checked((int)manifest.Length)]; manifest.ReadExactly(bytes);
            // Existing v2 applications keep their declared entries; new applications select their owned GUI launcher.
            var launcher = ApplicationManifest.Read(new(), bytes, null).Files.SingleOrDefault(file => file.RelativePath == ApplicationTree.LauncherEntry);
            if (launcher is not null)
            {
                executable = Path.Combine(Path.GetDirectoryName(host)!, ApplicationTree.LauncherEntry);
                imageSha256 = launcher.Sha256!;
            }
        }
        return CreateForInstallation(instance, database, lease, resources, recovery, executable, new StartupRegistrationEffects(), imageSha256);
    }

    internal static HostSettingsService CreateForInstallation(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceLeaseManager resources, RecoveryIndex recovery, string executable, IStartupRegistrationEffects effects, string? imageSha256 = null)
    {
        using var process = JobNative.OpenProcess(0x1000, false, Environment.ProcessId);
        var identity = ProcessIdentityReader.ReadMetadata(process, Environment.ProcessId);
        executable = PhysicalPathPolicy.NormalizeWithoutLinks(executable);
        using var image = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        var definition = new StartupRegistrationDefinition(instance.InstanceId, Guid.NewGuid(), identity.UserSid, executable,
            ["desktop", "--runtime-root", instance.RuntimeRoot, "--port", "saved"],
            PhysicalPathPolicy.NormalizeWithoutLinks(Path.GetDirectoryName(executable)!), DateTimeOffset.UtcNow);
        var template = new StartupRegistrationCommand(definition, imageSha256 ?? Convert.ToHexStringLower(SHA256.HashData(image)));
        var coordinator = new StartupRegistrationCoordinator(instance, database, lease, recovery, effects, template);
        var values = new HostSettingsStore(database).Read().Values;
        return new(instance, database, lease, resources, new(values.Desktop.Mode, values.Service, values.Updates.PluginSource), coordinator);
    }
}
