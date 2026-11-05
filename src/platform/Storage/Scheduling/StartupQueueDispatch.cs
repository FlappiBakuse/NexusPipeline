using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Scheduling;

[SupportedOSPlatform("windows")]
internal sealed class StartupQueueDispatch(RuntimeInstance instance, StateDatabase database, InstanceLease lease,
    StartupQueueStore store, ExecutionAdmissionService execution, TimeProvider clock)
{
    private bool _registered;
    internal void Initialize()
    {
        if (!_registered) { _ = store.Commit(); _registered = true; }
    }

    internal void Tick(CancellationToken cancellation, bool canCommit = true)
    {
        cancellation.ThrowIfCancellationRequested();
        if (canCommit) Initialize();
        foreach (var saved in store.ReadWaiting())
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                using var origin = PreparedExecutionOrigin.CaptureStartup(instance, database, lease, saved.Occurrence.OccurrenceId, revalidate: true);
                _ = execution.StartScheduled(origin, clock.GetUtcNow());
            }
            catch (ExecutionSelectionException failure) when (failure.Message is "execution_resource_conflict" or "execution_host_unavailable" or "execution_admission_inbox_full" or
                "execution_catalog_revision_conflict" or "execution_settings_revision_conflict") { }
            catch (ExecutionSelectionException failure)
            {
                store.Block(saved, failure.Message == "execution_settings_revision_conflict" ? "schedule_settings_changed" : "schedule_definition_changed");
            }
            catch (ConfigurationException) { store.Block(saved, "schedule_inputs_unavailable"); }
            catch (IOException failure) when (failure.Message == "execution_budget_continuity_unknown")
            { store.Block(saved, "schedule_budget_continuity_unknown"); }
            catch (IOException failure) when (failure.Message is "execution_image_changed" or "execution_directory_changed" or
                "execution_physical_claims_changed" or "execution_extension_provider_unavailable")
            { store.Block(saved, "schedule_inputs_unavailable"); }
        }
    }
}
