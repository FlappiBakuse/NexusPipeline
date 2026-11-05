using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record TargetStoppedDocument(string Format, int Version, ExecutionTargetBinding Binding,
    ProcessIdentity? Root, OwnedTargetInventory Inventory);

[SupportedOSPlatform("windows")]
internal sealed class PreparedStoppedTarget
{
    private readonly OwnedWorkerJob _job;
    private readonly OwnedTargetInventory _inventory;

    private PreparedStoppedTarget(OwnedWorkerJob job, OwnedTargetInventory inventory)
    { _job = job; _inventory = inventory; }

    internal static async Task<PreparedStoppedTarget> StopAsync(OwnedWorkerJob actualJob, TimeSpan timeout)
    {
        if (!await actualJob.StopAsync(timeout).ConfigureAwait(false)) throw new IOException("execution_target_stop_unconfirmed");
        var inventory = actualJob.CaptureTargetInventory();
        if (inventory.ActiveProcesses != 0 || !inventory.LivingFamily.IsEmpty || inventory.AllFamily.IsDefault ||
            inventory.AllFamily.Length != inventory.TotalProcesses || inventory.AllFamily.Length != inventory.RetainedKernelObjects) throw new IOException("execution_target_stop_unconfirmed");
        return new(actualJob, inventory);
    }

    internal T Use<T>(OwnedWorkerJob actualJob, Func<OwnedTargetInventory, T> callback)
    {
        if (!ReferenceEquals(actualJob, _job)) throw new IOException("execution_target_stop_owner_unbound");
        var current = actualJob.CaptureTargetInventory();
        if (current.ActiveProcesses != 0 || !current.LivingFamily.IsEmpty || current.TotalProcesses != _inventory.TotalProcesses ||
            current.RetainedKernelObjects != _inventory.RetainedKernelObjects ||
            !current.AllFamily.SequenceEqual(_inventory.AllFamily)) throw new IOException("execution_target_stop_changed");
        return callback(_inventory);
    }
}
