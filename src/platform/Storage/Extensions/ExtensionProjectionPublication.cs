using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal static class ExtensionProjectionPublication
{
    internal static void PublishIfDirty(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
        ContractReader contracts, PayloadApplyRoots roots, ResourceLeaseManager resources, RecoveryIndex recovery)
    {
        lease.VerifyHeld(instance);
        if (!resources.UsesRecoveryIndex(recovery)) throw new IOException("extension_publication_unbound");
        lease.PublishExecution(() =>
        {
            var registry = new RecoveryJournalRegistry(database);
            if (!registry.ReadActiveSnapshot().IsReady)
            {
                new RecoveryCoordinator(registry,
                    new AdmissionPublicationEvidence(instance, database, contracts, roots), recovery).VerifyAndPublish();
                ConfigurationRecoveryHolds.Publish(database, recovery, roots);
            }
            return true;
        });
    }
}
