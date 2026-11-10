using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Host.Lifecycle;

[SupportedOSPlatform("windows")]
internal static class RestoredHostStart
{
    internal static async Task<int> RunAsync(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        PayloadApplyRoots roots, UpdateFeedVerifier verifier, InstanceLease lease,
        HelperBootstrapConnection parent, MaintenanceEntryArguments options, MaintenanceStartupBinding binding)
    {
        lease.VerifyHeld(instance);
        using var inputs = MaintenanceStartupInputs.Hold(instance, database, contracts, roots, verifier, binding, CancellationToken.None);
        if (inputs.Restored is not { } restored) throw new IOException("maintenance_original_not_restored");
        parent.Actor.RequireRestoredBound(inputs);
        using var startup = new MaintenanceStartupServer(binding);
        using var successor = new MaintenanceStartupJob(parent.Origin, _ => { });
        // The restored Host validates its complete payload before connecting and preparing its receipt.
        using var preparation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var entry = PhysicalPathPolicy.NormalizeWithoutLinks(restored.OriginalHost.ImagePath);
        var next = options with { PipeName = startup.PipeName, JobName = successor.Name };
        OwnedWorkerIdentity? root = null;
        try
        {
            root = successor.Start(entry, next.ToArguments("maintenance-start"), Path.GetPathRoot(entry)!,
                instance.InstanceId, options.HandoffId,
                actual => parent.Actor.RegisterRestoredSuspended(inputs, database, lease, successor, actual), role: MaintenanceStartupRole.HostRoot);
            lease.Dispose();
            var identity = new ProcessIdentity(root.ProcessId, root.CreatedAtUtc, root.ImagePath, root.ImageSha256, root.UserSid, root.IsElevated);
            var reply = await startup.ReceivePreparedAsync(successor, identity, parent.Actor, preparation.Token, inputs);
            using var verification = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var receipt = new StartupReceiptStore(instance, database, contracts, roots, verifier)
                .ReadPrepared(startup.Challenge, reply, identity, successor.Name, verification.Token);
            await startup.ReleasePreparedAsync(successor, reply, receipt, database, verification.Token);
            return 0;
        }
        catch
        {
            if (root is not null && !await successor.StopAsync(TimeSpan.FromSeconds(10)))
                throw new IOException("maintenance_restored_host_stop_pending");
            throw;
        }
    }
}
