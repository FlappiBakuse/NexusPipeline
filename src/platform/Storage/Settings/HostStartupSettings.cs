using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Processes;
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
        => CreateForInstallation(instance, database, lease, resources, recovery,
            ProcessIdentityReader.Read(Environment.ProcessId).ImagePath, new StartupRegistrationEffects());

    internal static HostSettingsService CreateForInstallation(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ResourceLeaseManager resources, RecoveryIndex recovery, string executable, IStartupRegistrationEffects effects)
    {
        var identity = ProcessIdentityReader.Read(Environment.ProcessId);
        executable = PhysicalPathPolicy.NormalizeWithoutLinks(executable);
        using var image = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        var definition = new StartupRegistrationDefinition(instance.InstanceId, Guid.NewGuid(), identity.UserSid, executable,
            ["desktop", "--runtime-root", instance.RuntimeRoot, "--port", "saved"],
            PhysicalPathPolicy.NormalizeWithoutLinks(Path.GetDirectoryName(executable)!), DateTimeOffset.UtcNow);
        var template = new StartupRegistrationCommand(definition, Convert.ToHexStringLower(SHA256.HashData(image)));
        var coordinator = new StartupRegistrationCoordinator(instance, database, lease, recovery, effects, template);
        var values = new HostSettingsStore(database).Read().Values;
        return new(instance, database, lease, resources, new(values.Desktop.Mode, values.Service, values.Updates.PluginSource), coordinator);
    }
}
