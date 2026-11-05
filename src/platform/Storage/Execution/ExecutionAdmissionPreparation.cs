using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Operations;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Journals;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class ExecutionAdmissionPreparation(object owner, RuntimeInstance instance, StateDatabase database,
    ContractReader contracts, InstanceLease lease, ResourceLeaseManager resources, RecoveryIndex index, ExecutionPlanBuilder builder, PayloadApplyRoots roots)
{
    internal PreparedExecutionAdmission Freeze(OperationIdentity identity, ExecutionAdmissionDraft draft, ResourceOperationLease root, PreparedExecutionRetry? retry = null)
    {
        var held = new List<IDisposable>();
        try
        {
            var total = retry is null ? builder.Freeze(draft.Draft, root) : builder.FreezeRetry(draft.Draft, retry); held.Add(total);
            var providers = builder.HoldProviders(total.Plan, held);
            var wire = total.Plan.ReadWire();
            JournalCapacityPolicy.Require(wire);
            PreparedExecutionNetwork? network = null;
            if (wire.JudgementNetwork is not null)
            {
                var credential = new SecretFieldService(instance, database, lease, resources,
                    index, contracts).PrepareProxyCredential(root, identity.OperationId);
                try { network = new(wire, identity.OperationId, credential); }
                catch { credential.Dispose(); throw; }
                held.Add(network);
            }
            var configurations = new Dictionary<string, PreparedConfigurationRun>(StringComparer.Ordinal);
            var preparation = new ConfigurationRunPreparation(instance, database, contracts, lease, resources);
            var revisions = new ConfigurationRevisionStore(instance, database, contracts);
            foreach (var step in wire.Steps.Where(step => step.ConfigurationTargets.Count != 0))
            {
                var slots = wire.ConfigurationSlots.Where(slot => slot.StepId == step.StepId).ToArray();
                var bindingRevision = wire.SourceRevisions.Single(item => item.EntityKind == "binding" && item.EntityId == step.BindingId).Revision;
                var targets = slots.Select(slot =>
                {
                    var address = PlanConfigurationSlots.Address(slot);
                    var saved = retry is null ? revisions.Read(address) ?? throw new ConfigurationException("configuration_required") :
                        (retry.ConfigurationOrigin ?? throw new IOException("configuration_retry_source_unbound")).Sources(step.StepId)[address].State.Source;
                    if (saved.Revision != slot.ExpectedRevision) throw new ConfigurationException("configuration_revision_conflict");
                    return new ConfigurationRunResource(Guid.NewGuid(), Guid.NewGuid(), new(address, slot.ExpectedRevision, saved.Contract, slot.TargetPath)
                        { WritePolicy = new([""], [saved.Tree.Shape]) }, retry is null
                            ? ExecutionTaskProjection.Rules(wire, step, saved, new(instance, contracts), contracts)
                            : retry.ConfigurationOrigin!.Sources(step.StepId)[address].State.Tasks.Select(item => item.Rule).ToImmutableArray());
                }).ToImmutableArray();
                var request = new ConfigurationRunRequest(bindingRevision, true, targets) { SyncInitially = retry is null, SyncAtFinish = step.UpdateConfigurationAtFinish };
                var prepared = retry is null ? preparation.Prepare(root, identity.OperationId, total.Plan, step.StepId, request) :
                    preparation.PrepareHistorical(root, identity.OperationId, total.Plan, step.StepId, request,
                        retry.ConfigurationOrigin ?? throw new IOException("configuration_retry_source_unbound"));
                configurations.Add(step.StepId, prepared); held.Add(prepared);
            }
            var guards = new List<IDisposable>();
            var candidate = BuildAttempt(identity, draft.Draft.Explanation.Selection.Request, total, configurations, guards, held, retry);
            var attempt = new AttemptPlanCodec(instance, contracts, new(instance, contracts)).Freeze(candidate); held.Add(attempt);
            return new(owner, identity, total, attempt, new ReadOnlyDictionary<string, PreparedConfigurationRun>(configurations), guards.AsReadOnly(), held, draft.TimeBudget, network, roots, providers, draft.FirstPreparedAtUtc)
            {
                Display = ExecutionRunDisplay.From(draft.Draft.Explanation.Selection, wire.RetryPolicy.MaximumAttempts, draft.Automatic), Emulators = builder.Emulators
            };
        }
        catch { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); throw; }
    }

    private RunAttemptPlan BuildAttempt(OperationIdentity identity, NexusPipeline.Core.Execution.ExecutionSelectionRequest request, PreparedExecutionPlan total,
        IReadOnlyDictionary<string, PreparedConfigurationRun> configurations, List<IDisposable> guards, List<IDisposable> held, PreparedExecutionRetry? retry)
    {
        var wire = total.Plan.ReadWire(); var attemptId = (retry?.Frozen.NextAttemptId ?? Guid.NewGuid()).ToString("D");
        var journalId = retry?.Frozen.NextJournalId ?? Guid.NewGuid();
        var blobs = new ImmutableBlobStore(instance, contracts);
        var start = blobs.PersistPlan(StartRequestCodec.Encode(identity, request, Guid.Parse(wire.RunId), journalId, retry?.Intent.PreviousOperationId));
        held.Add(blobs.OpenVerified(start));
        var archive = new FileTreeArchive(new(SnapshotBudget.Default), new(instance, contracts));
        var logs = wire.Steps.SelectMany(step => step.LogTargets).Select(log => log.ScopePath).Distinct(StringComparer.OrdinalIgnoreCase).Select(path =>
        {
            var before = archive.Capture(path, default);
            var parent = PhysicalObjectIdentity.Read(Path.GetDirectoryName(path)!);
            var guard = TreeReadLease.Acquire(path, parent, before); guards.Add(guard); held.Add(guard);
            return new RunAttemptPlanInitialLogsItem { Path = path, ParentIdentity = FileTreeProtocol.ToWire(parent),
                Before = FileTreeProtocol.ToWire(before.Snapshot), OriginalObjects = before.OriginalObjects.Select(item =>
                    new FileTreePhysicalObject { RelativePath = item.RelativePath, Identity = FileTreeProtocol.ToWire(item.Identity) }).ToArray() };
        }).ToArray();
        return new()
        {
            Format = "nexus.execution.run-attempt-plan", Version = 1, InstanceId = wire.InstanceId, RunId = wire.RunId,
            OperationId = identity.OperationId.ToString("D"), JournalId = journalId.ToString("D"), AttemptId = attemptId, AttemptOrdinal = retry?.Frozen.NextOrdinal ?? 1,
            StartRequest = new() { RelativePath = start.RelativePath, SizeBytes = start.SizeBytes, Sha256 = start.Sha256 },
            ExecutionPlan = new() { RelativePath = total.Reference.RelativePath, SizeBytes = total.Reference.SizeBytes, Sha256 = total.Reference.Sha256 },
            ResourceKeys = wire.ResourceKeys, OutputDirectory = $"runs/{wire.RunId}/{attemptId}", LogDirectory = $"logs/{wire.RunId}/{attemptId}",
            ConfigurationPlans = wire.Steps.Where(step => configurations.ContainsKey(step.StepId)).SelectMany(step => configurations[step.StepId].Use(children =>
                children.Select(child => new RunAttemptPlanConfigurationPlansItem { StepId = step.StepId,
                    Plan = new() { RelativePath = child.Reference.RelativePath, SizeBytes = child.Reference.SizeBytes, Sha256 = child.Reference.Sha256 } }).ToArray())).ToArray(),
            InitialLogs = logs
        };
    }
}
