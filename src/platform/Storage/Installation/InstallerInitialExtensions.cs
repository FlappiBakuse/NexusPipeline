using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Installation;

[SupportedOSPlatform("windows")]
internal static class InstallerInitialExtensions
{
    internal static async Task InstallAsync(RuntimeInstance instance, InstanceLease lease, string application, string source)
    {
        var contracts = new ContractReader();
        var roots = new PayloadApplyRoots(application, Path.Combine(instance.RuntimeRoot, "extensions"));
        var database = File.Exists(Path.Combine(instance.RuntimeRoot, "state-initialization.guard"))
            ? StateDatabase.OpenCurrent(instance, lease) : StateDatabase.CreateFresh(instance, lease);
        Directory.CreateDirectory(roots.ExtensionPayloadRoot);
        var recovery = new StartupJournalRecovery(instance, contracts, database, roots).Recover(lease);
        var resources = new ResourceLeaseManager(new HostLifecycle(recovery, TimeProvider.System));
        await using var runtime = new ExtensionRuntimeLifecycle(instance, database, lease, contracts, roots, resources,
            recovery, new ExecutionProviderRegistry(), ExtensionReleaseTrust.Configured());
        await new InitialExtensionInstaller(instance, database, lease, contracts, roots, resources, recovery, runtime)
            .InitializeAsync(source, CancellationToken.None).ConfigureAwait(false);
    }
}
