using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed record ConfigurationSceneResource(Guid OperationId, Guid JournalId, JournalKind Kind, ConfigurationAddress Address, string PlanPath, string PlanDigest, long? ExpectedRevision = null);

internal sealed class ConfigurationSceneEvidence(RuntimeInstance instance, StateDatabase database, ContractReader contracts)
{
    internal IDisposable Hold(IReadOnlyList<ConfigurationSceneResource> resources, bool installed,
        IReadOnlyDictionary<ConfigurationAddress, FileTreeSnapshot>? candidates = null)
    {
        var held = new Held();
        try
        {
            var registry = new RecoveryJournalRegistry(database);
            var journals = new JournalStore(instance, contracts);
            var writerStore = new JournalWriterStore(database);
            var scanner = new FileTreeScanner(SnapshotBudget.Default);
            var swap = new ConfigurationSwap(scanner, new(scanner, new(instance, contracts)));
            foreach (var resource in resources)
            {
                if (registry.FindJournal(resource.JournalId) is not { } registered || registered.OperationId != resource.OperationId ||
                    registered.Kind != resource.Kind || registered.Kind is not (JournalKind.ConfigEdit or JournalKind.ConfigExchange) ||
                    registered.PlanPath != resource.PlanPath || registered.PlanDigest != resource.PlanDigest ||
                    !registered.ResourceKeys.Contains(ConfigurationRevisionService.ResourceKey(resource.Address), StringComparer.Ordinal))
                    throw new IOException("configuration_edit_journal_unbound");
                var physical = journals.Read(resource.JournalId);
                if (physical.Phase != registered.Phase || physical.Revision != registered.Revision || physical.PlanDigest != registered.PlanDigest ||
                    !physical.Root.GetProperty("resourceKeys").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal).SequenceEqual(registered.ResourceKeys) ||
                    physical.Phase != (installed ? JournalPhase.Applied : JournalPhase.Committed))
                    throw new IOException("configuration_edit_journal_not_settled");
                var binding = ConfigurationJournalBinding.Read(instance, physical, contracts, database);
                var observed = writerStore.ObserveForCurrentExecutor(resource.JournalId);
                if (!observed.IsComplete || observed.State != WriterState.Stopped) throw new IOException("configuration_writer_stop_unconfirmed");
                var writers = new ConfigurationWriterObservation(instance.InstanceId, resource.JournalId, true, WriterState.Stopped);
                if (installed)
                {
                    var workspace = binding.Prepared ?? throw new IOException("configuration_edit_workspace_missing");
                    held.Add(TreeReadLease.Acquire(binding.Plan.TargetPath, binding.Plan.ParentIdentity, binding.Plan.Candidate with { OriginalObjects = workspace.CandidateObjects }));
                }
                else
                {
                    if (binding.Prepared is { } workspace) swap.VerifyRestored(workspace, writers);
                    else if (!physical.Root.GetProperty("receipts").EnumerateArray().Any(receipt =>
                        receipt.GetProperty("stepId").GetString() == "verify-no-effect" && receipt.GetProperty("effect").GetString() == "notExecuted"))
                        throw new IOException("configuration_edit_no_effect_unverified");
                    held.Add(TreeReadLease.Acquire(binding.Plan.TargetPath, binding.Plan.ParentIdentity, binding.Plan.Before));
                }
                if (candidates is null) continue;
                var candidate = candidates[resource.Address];
                var expected = binding.Prepared?.Plan.Candidate.Snapshot ?? binding.Plan.Candidate.Snapshot;
                if (binding.Plan.EditPolicy?.Adapter is { } rule)
                {
                    var transformed = AdapterEditHook.Transform(new(instance, contracts), resource.OperationId, resource.Address,
                        resource.ExpectedRevision ?? throw new IOException("configuration_edit_adapter_revision_missing"), expected, rule, "editCommit");
                    expected = transformed.Tree;
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(transformed.Content);
                }
                if (expected.Shape != candidate.Shape || !expected.Entries.SequenceEqual(candidate.Entries))
                    throw new IOException("configuration_edit_candidate_not_frozen");
            }
            return held;
        }
        catch { held.Dispose(); throw; }
    }

    private sealed class Held : IDisposable
    {
        private readonly List<IDisposable> _held = [];
        internal void Add(IDisposable lease) => _held.Add(lease);
        public void Dispose() { for (var index = _held.Count - 1; index >= 0; index--) _held[index].Dispose(); _held.Clear(); }
    }
}
