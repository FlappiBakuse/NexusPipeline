using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage;

public sealed class InstanceLease : IDisposable
{
    private readonly object _extensionGate = new();
    private NexusPipeline.Platform.Extensions.ExtensionActivator? _extensionActivator;
    private NexusPipeline.Core.Maintenance.ResourceLeaseManager? _extensionResources;
    private readonly FileStream _lock;
    private readonly FileStream _identity;
    private readonly ExecutionPublicationQueue _executionPublications = new();
    public RuntimeInstance Instance { get; }
    internal Guid StartupGeneration { get; } = Guid.NewGuid();
    internal Execution.RunBudgetOwner RunBudgets { get; }
    internal Execution.LiveLogHub LiveLogs { get; }

    private InstanceLease(RuntimeInstance instance, FileStream instanceLock, FileStream identity)
    {
        Instance = instance;
        RunBudgets = new(this); LiveLogs = new(this);
        _lock = instanceLock;
        _identity = identity;
    }

    public static InstanceLease Acquire(RuntimeInstance instance, ContractReader contracts)
        => AcquireCore(instance, contracts, installer: false);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static InstanceLease AcquireInstaller(RuntimeInstance instance, ContractReader contracts,
        NexusPipeline.Platform.Processes.ProcessIdentity setup)
    {
        Installation.InstallerCheckpointStore.RequireCaller(setup);
        return AcquireCore(instance, contracts, installer: true);
    }

    private static InstanceLease AcquireCore(RuntimeInstance instance, ContractReader contracts, bool installer)
    {
        if (!installer && OperatingSystem.IsWindows()) Installation.InstallerRuntimeGate.RequireAvailable(instance.RuntimeRoot);
        var actual = new InstanceRoot(contracts).OpenExisting(instance.RuntimeRoot);
        if (actual.InstanceId != instance.InstanceId) throw new IOException("runtime_identity_mismatch");
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(actual.RuntimeRoot, "instance.lock"));
        if (File.Exists(path) && new FileInfo(path).Length != 0) throw new IOException("unrecognized_instance_lock");
        var identity = new FileStream(Path.Combine(actual.RuntimeRoot, "instance.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (new InstanceRoot(contracts).OpenExisting(actual.RuntimeRoot) != actual)
                throw new IOException("runtime_identity_changed");
            var instanceLock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                if (!installer && OperatingSystem.IsWindows()) Installation.InstallerRuntimeGate.RequireAvailable(instance.RuntimeRoot);
                if (instanceLock.Length == 0) return new(actual, instanceLock, identity);
                throw new IOException("unrecognized_instance_lock");
            }
            catch { instanceLock.Dispose(); throw; }
        }
        catch { identity.Dispose(); throw; }
    }

    public void Dispose()
    {
        _executionPublications.Close(() => { RunBudgets.Close(); LiveLogs.Close(); _lock.Dispose(); _identity.Dispose(); });
    }

    internal void BindExtensionActivator(NexusPipeline.Platform.Extensions.ExtensionActivator owner,
        NexusPipeline.Core.Maintenance.ResourceLeaseManager resources)
    {
        VerifyHeld(Instance);
        lock (_extensionGate)
        {
            if (_extensionActivator is not null && (!ReferenceEquals(owner, _extensionActivator) ||
                !ReferenceEquals(resources, _extensionResources)))
                throw new IOException("extension_scope_inventory_already_bound");
            _extensionActivator = owner; _extensionResources = resources;
        }
    }

    internal void RequireExtensionActivator(NexusPipeline.Platform.Extensions.ExtensionActivator owner,
        NexusPipeline.Core.Maintenance.ResourceLeaseManager resources)
    {
        VerifyHeld(Instance);
        lock (_extensionGate)
            if (!ReferenceEquals(owner, _extensionActivator) || !ReferenceEquals(resources, _extensionResources))
                throw new IOException("extension_scope_inventory_unbound");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal void RequireReplaceableNative(string extensionId, string targetPath)
    {
        VerifyHeld(Instance);
        lock (_extensionGate) _extensionActivator?.RequireReplaceableNative(extensionId);
        NexusPipeline.Platform.Extensions.InstalledNativeSlot.RequireUnmappedTree(targetPath);
    }

    internal void RequirePublicationEntry() => _executionPublications.RequireNotReentrant();
    internal int ExecutionPublicationCount => _executionPublications.PendingCount;
    internal Task WhenExecutionPublicationsReleased => _executionPublications.Released;

    internal T PublishExecution<T>(Func<T> callback)
    {
        VerifyHeld(Instance);
        return _executionPublications.Run(() => { VerifyHeld(Instance); return callback(); });
    }

    internal void PublishExecution(Action callback) => PublishExecution(() => { callback(); return true; });

    internal void VerifyHeld(RuntimeInstance instance)
    {
        if (Instance != instance || !_lock.CanWrite || !_identity.CanRead)
            throw new IOException("runtime_exclusive_lease_required");
    }
}
