using System.Security.Cryptography;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Settings;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Testing;

internal static class StartupCrashFixture
{
    internal static int Run(string[] args)
    {
        if (args is not [var root, var pointValue, var enableValue, var report] ||
            !Enum.TryParse<StartupEffectBoundary>(pointValue, out var target) || !bool.TryParse(enableValue, out var enable)) return 2;
        OwnedFixtureEvidence.RequirePath(root); OwnedFixtureEvidence.RequirePath(report);
        try
        {
            var instance = new InstanceRoot(new()).OpenOrCreate(Path.Combine(root, "runtime"));
            var database = StateDatabase.CreateFresh(instance);
            using var lease = InstanceLease.Acquire(instance, new());
            var recovery = new StartupJournalRecovery(instance, new(), database,
                new(root, Path.Combine(instance.RuntimeRoot, "extensions"))).Recover(lease);
            var resources = new ResourceLeaseManager(new HostLifecycle(recovery, TimeProvider.System));
            var path = Path.Combine(root, "controlled-task.xml");
            var effects = new ControlledStartupRegistration(path);
            var executable = Environment.ProcessPath!;
            var template = new StartupRegistrationCommand(new(instance.InstanceId, Guid.NewGuid(), ProcessIdentityReader.Read(Environment.ProcessId).UserSid,
                executable, ["--fixture-only"], root, DateTimeOffset.UtcNow), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable))));
            var operation = Guid.NewGuid();
            var observer = new Observer(instance, operation, target, enable, report, path);
            var coordinator = new StartupRegistrationCoordinator(instance, database, lease, recovery, effects, template, observer);
            var defaults = HostSettings.Default;
            var service = new HostSettingsService(instance, database, lease, resources,
                new(defaults.Desktop.Mode, defaults.Service, defaults.Updates.PluginSource, false), coordinator);
            if (!enable)
            {
                var initial = new HostSettingsChange(0, defaults with { AutoStart = true });
                service.Save(HostSettingsStore.Identity(Guid.NewGuid(), "fixture", initial), initial, DateTimeOffset.UtcNow);
            }
            var change = new HostSettingsChange(enable ? 0 : 1, defaults with { AutoStart = enable });
            service.Save(HostSettingsStore.Identity(operation, "fixture", change), change, DateTimeOffset.UtcNow);
            return 1;
        }
        catch (Exception error)
        {
            if (!File.Exists(report)) OwnedFixtureEvidence.WriteReport(report, new { status = "refused", code = error.Message, errorType = error.GetType().FullName });
            return 1;
        }
    }

    private sealed class Observer(RuntimeInstance instance, Guid operation, StartupEffectBoundary target,
        bool enable, string report, string taskPath) : IStartupEffectObserver
    {
        public void Reached(StartupEffectBoundary boundary, Guid operationId)
        {
            if (operationId != operation || boundary != target) return;
            OwnedFixtureEvidence.WriteReport(report, new { status = "faultReady", boundary = boundary.ToString(),
                instance, operationId, enable, taskPath, processId = Environment.ProcessId });
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
