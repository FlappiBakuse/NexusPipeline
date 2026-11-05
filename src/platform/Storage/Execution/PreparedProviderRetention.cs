using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class PreparedProviderRetention
{
    private readonly ProviderLauncherStore _store;
    private readonly OwnedWorkerJob _job;
    internal ProviderLauncherBinding Binding { get; }
    internal BlobReference Reference { get; }

    internal PreparedProviderRetention(ProviderLauncherStore store, OwnedWorkerJob job,
        ProviderLauncherBinding binding, BlobReference reference)
    { _store = store; _job = job; Binding = binding; Reference = reference; }

    internal T Use<T>(OwnedWorkerJob actualJob, OwnedTargetInventory inventory, Func<T> effect)
    {
        if (!ReferenceEquals(actualJob, _job)) throw new IOException("execution_provider_launcher_owner_unbound");
        return _store.UseRetention(Binding, actualJob, Reference, inventory, effect);
    }
}
