using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class PreparedAndroidAuthority
{
    private readonly AndroidControlStore _owner;
    private readonly OwnedWorkerJob _job;
    private readonly AndroidCommandRequest _request;
    internal AndroidCommandRequest Request => AndroidControlStore.Copy(_request);

    internal PreparedAndroidAuthority(AndroidControlStore owner, OwnedWorkerJob actualJob,
        AndroidCommandRequest frozen)
    { _owner = owner; _job = actualJob; _request = AndroidControlStore.Copy(frozen); }

    internal T Use<T>(OwnedWorkerJob actualJob, Func<AndroidCommandRequest, T> callback)
    {
        if (!ReferenceEquals(actualJob, _job)) throw new IOException("execution_android_command_native_owner_unbound");
        _owner.RequireIssued(this, actualJob, _request);
        return callback(AndroidControlStore.Copy(_request));
    }
}

[SupportedOSPlatform("windows")]
internal sealed class PreparedAndroidObservation
{
    private readonly AndroidControlProcess _owner;
    internal AndroidCommandObservation Document { get; }

    internal PreparedAndroidObservation(AndroidControlProcess owner, AndroidCommandObservation document)
    { _owner = owner; Document = document; }

    internal T Use<T>(Func<AndroidCommandObservation, T> callback) => _owner.UseObserved(this, callback);
}

[SupportedOSPlatform("windows")]
internal sealed class PreparedAndroidCollection
{
    private readonly AndroidControlProcess _owner;
    internal PreparedAndroidObservation Observation { get; }
    internal OwnedTargetInventory Inventory { get; }
    internal BlobReference? Retention => _owner.Retention;

    internal PreparedAndroidCollection(AndroidControlProcess owner,
        PreparedAndroidObservation observation, OwnedTargetInventory actualInventory)
    { _owner = owner; Observation = observation; Inventory = actualInventory; }

    internal T Use<T>(Func<AndroidCommandObservation, OwnedTargetInventory, T> callback) =>
        _owner.UseCollected(this, callback);
}
