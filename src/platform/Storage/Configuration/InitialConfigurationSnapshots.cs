using System.Collections.Immutable;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Scheduling;

namespace NexusPipeline.Platform.Storage.Configuration;

[SupportedOSPlatform("windows")]
internal sealed class InitialConfigurationSnapshots(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
    InstanceLease lease, ResourceLeaseManager resources)
{
    internal void Establish(string subject, ExecutionSelectionRequest request, DateTimeOffset createdAt)
    {
        lease.VerifyHeld(instance);
        var catalog = new AutomationDefinitionStore(database).Read();
        var settings = new HostSettingsStore(database).Read();
        var selection = ExecutionSelectionPolicy.Select(catalog, settings, request, ExecutionBindingSuccesses.Today(database, createdAt));
        var revisions = new ConfigurationRevisionStore(instance, database, contracts);
        foreach (var item in selection.Items.Where(item => item.Disposition == ExecutionSelectionDisposition.Candidate &&
            item.Script.Kind == AutomationKind.Generic).DistinctBy(item => item.Binding.Id))
        {
            var missing = item.Script.ConfigurationPaths.Where(path => revisions.Read(new(item.Binding.Id,
                GenericConfigurationPolicy.ResourceId(path))) is null).Select(path =>
                    new ConfigurationImportResource(GenericConfigurationPolicy.ResourceId(path), 0)).ToImmutableArray();
            if (missing.IsEmpty) continue;
            new ConfigurationImportService(instance, database, contracts, lease, resources).Import(subject,
                new(Guid.NewGuid(), item.Binding.Id, item.Binding.Revision, item.Script.Revision, missing), createdAt,
                validateGenericContent: true);
        }
    }
}
