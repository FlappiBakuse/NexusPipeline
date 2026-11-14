using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed class ConfigurationRunPreparation
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _instanceLease;
    private readonly ResourceLeaseManager _resources;

    public ConfigurationRunPreparation(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease instanceLease, ResourceLeaseManager resources)
    {
        instanceLease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot)
            throw new IOException("configuration_store_instance_unbound");
        _instance = instance; _database = database; _contracts = contracts; _instanceLease = instanceLease; _resources = resources;
    }

    public IReadOnlyList<ResourceClaim> Claims(FrozenExecutionPlan execution, string stepId, ConfigurationRunRequest request)
    {
        _instanceLease.VerifyHeld(_instance);
        request = RunRequestPolicy.Freeze(request);
        var step = RequireStep(execution, stepId, request);
        ConfigurationTargetPaths.Validate(_instance, request.Resources.Select(item => item.Target).ToArray());
        return Claims(step, request, request.Resources.SelectMany(item => PhysicalResourceAdmission.Read(item.Target.TargetPath)));
    }

    public PreparedConfigurationRun Prepare(ResourceOperationLease rootLease, Guid ownerOperationId, FrozenExecutionPlan execution,
        string stepId, ConfigurationRunRequest request, CancellationToken cancellationToken = default)
        => PrepareCore(rootLease, ownerOperationId, execution, stepId, request, null, cancellationToken);

    internal PreparedConfigurationRun PrepareHistorical(ResourceOperationLease rootLease, Guid ownerOperationId, FrozenExecutionPlan execution,
        string stepId, ConfigurationRunRequest request, PreparedRetryOrigin origin, CancellationToken cancellationToken = default)
        => PrepareCore(rootLease, ownerOperationId, execution, stepId, request, origin, cancellationToken);

    private PreparedConfigurationRun PrepareCore(ResourceOperationLease rootLease, Guid ownerOperationId, FrozenExecutionPlan execution,
        string stepId, ConfigurationRunRequest request, PreparedRetryOrigin? origin, CancellationToken cancellationToken)
    {
        _instanceLease.VerifyHeld(_instance);
        request = RunRequestPolicy.Freeze(request);
        var step = RequireStep(execution, stepId, request, origin?.TaskRetry);
        if (step.ExclusiveProgram == true)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            Processes.ExclusiveProgramAdmission.RequireStopped(step.ExecutablePath);
        }
        var historical = origin?.ForStep(_instance, ownerOperationId, execution, stepId, request);
        if (ownerOperationId == Guid.Empty || request.Resources.Any(item => item.OperationId == ownerOperationId))
            throw new ConfigurationException("configuration_run_operation_invalid");
        ConfigurationTargetPaths.Validate(_instance, request.Resources.Select(item => item.Target).ToArray());
        var held = new List<IDisposable>(); var plans = new List<PreparedConfigurationPlan>();
        try
        {
            var files = PhysicalResourceAdmission.Prepare(request.Resources.Select(item => item.Target.TargetPath)); held.Add(files);
            var borrow = rootLease.Borrow(_resources, ownerOperationId, Claims(step, request, files.Claims)); held.Add(borrow);
            return borrow.Use(() =>
            {
                VerifySourceAuthority();
                var blobs = new ImmutableBlobStore(_instance, _contracts);
                var revisions = new ConfigurationRevisionStore(_instance, _database, _contracts);
                var archive = new FileTreeArchive(new(SnapshotBudget.Default), blobs);
                foreach (var resource in request.Resources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = historical is null ? revisions.Read(resource.Target.Address) ?? throw new ConfigurationException("configuration_required") :
                        historical[resource.Target.Address].State.Source;
                    RequireSource(source, step, resource.Target);
                    if (step.Provider is not null && step.Adapter is null)
                        ProviderConfigurationPolicy.RequireFrozenSource(execution.ReadWire(), step, source, blobs, _contracts);
                    if (step.Adapter is not null) ExecutionAdapterBinding.RequireFrozenConfiguration(execution.ReadWire(), step, source, blobs, _contracts);
                    ExecutionTaskProjection.RequireBoundRules(execution.ReadWire(), step, source, resource.Tasks, blobs, _contracts);
                    var captured = new ConfigurationTaskTransformation(blobs).Capture(resource.OperationId, source, resource.Tasks, cancellationToken);
                    ExecutionTaskProjection.RequireValues(step, captured);
                    var inputTree = origin?.TaskRetry is not null ? historical![resource.Target.Address].Publication.Revision.Tree :
                        historical?[resource.Target.Address].Input?.Tree;
                    using var state = new RunStateCodec(_instance, _contracts, blobs).Freeze(new(_instance.InstanceId,
                        ownerOperationId, resource.OperationId, source, request.ExpectedBindingRevision, request.AutoSyncEnabled, captured) { SyncInitially = request.SyncInitially, SyncAtFinish = request.SyncAtFinish,
                        InitialPublicationRevision = historical?[resource.Target.Address].Publication.Revision.Revision ?? source.Revision, InputTree = inputTree });
                    var selected = new ConfigurationTaskTransformation(blobs).Select(resource.OperationId, source, captured, cancellationToken, inputTree);
                    var targetPath = ConfigurationTargetPaths.Canonical(resource.Target.TargetPath);
                    var before = archive.Capture(targetPath, cancellationToken);
                    var candidate = new ArchivedFileTree(selected, [], selected.Entries.Where(entry => entry.Kind == FileObjectKind.File)
                        .Select(entry => new BlobReference($"blobs/content/{entry.Sha256}.bin", entry.SizeBytes, entry.Sha256!)).Distinct().ToArray());
                    var plan = new ConfigurationPlanCodec(_instance, _contracts, blobs).Freeze(new(_instance.InstanceId, resource.OperationId,
                        resource.JournalId, JournalKind.ConfigExchange, targetPath, PhysicalObjectIdentity.Read(Path.GetDirectoryName(targetPath)!), before, candidate)
                        { RunState = state.Reference, EditPolicy = resource.Target.WritePolicy, PhysicalClaims = PhysicalResourceAdmission.Read(targetPath) });
                    plans.Add(plan); held.Add(plan);
                }
                var observed = PhysicalResourceAdmission.Merge(request.Resources.SelectMany(item => PhysicalResourceAdmission.Read(item.Target.TargetPath)));
                if (!observed.SequenceEqual(files.Claims)) throw new IOException("configuration_file_admission_changed");
                VerifySourceAuthority();
                if (historical is null)
                    foreach (var resource in request.Resources) RequireSource(revisions.Read(resource.Target.Address)!, step, resource.Target);
                _instanceLease.VerifyHeld(_instance);
                return new PreparedConfigurationRun(_instance, ownerOperationId, request, borrow, plans, held);
            });
        }
        catch { for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose(); throw; }

        void VerifySourceAuthority()
        {
            if (origin is null) { VerifyDefinitions(execution.ReadWire(), step, request); return; }
            using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Automation | DatabaseDomain.Configuration | DatabaseDomain.Content);
            using var transaction = connection.BeginTransaction(deferred: true);
            origin.RequireCurrentInside(connection, transaction);
        }
    }

    private ExecutionPlanStep RequireStep(FrozenExecutionPlan execution, string stepId, ConfigurationRunRequest request, ExecutionTaskRetry? taskRetry = null)
    {
        var plan = execution.ReadWire();
        var step = plan.Steps.SingleOrDefault(item => item.StepId == stepId) ?? throw new IOException("configuration_run_step_unknown");
        if (plan.InstanceId != _instance.InstanceId.ToString("D") || step.BindingId is null || step.UserId is null ||
            request.Resources.Any(item => item.Target.Address.BindingId.ToString("D") != step.BindingId) ||
            !step.ConfigurationTargets.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(request.Resources.Select(item => item.Target.TargetPath)) ||
            request.AutoSyncEnabled && request.SyncAtFinish != step.UpdateConfigurationAtFinish ||
            step.EngineKind == "extensionWorker" && (!request.AutoSyncEnabled ||
                request.SyncAtFinish != step.UpdateConfigurationAtFinish))
            throw new IOException("configuration_run_step_unbound");
        var slots = plan.ConfigurationSlots.Where(slot => slot.StepId == stepId).ToArray();
        if (slots.Length != request.Resources.Length || request.Resources.Any(resource => !slots.Any(slot =>
                PlanConfigurationSlots.Address(slot) == resource.Target.Address && slot.TargetPath == resource.Target.TargetPath &&
                slot.ExpectedRevision == resource.Target.ExpectedRevision)))
            throw new IOException("configuration_run_slots_unbound");
        var known = step.Tasks.Select(item => item.TaskId).ToHashSet(StringComparer.Ordinal);
        foreach (var resource in request.Resources) ExecutionTaskProjection.RequireRules(step, resource.Target.Address.ResourceId, resource.Tasks);
        foreach (var rule in request.Resources.SelectMany(item => item.Tasks))
        {
            var authorized = rule.Declaration.AuthorizedIds.ToHashSet(StringComparer.Ordinal);
            if (!authorized.IsSubsetOf(known) || !rule.SelectedIds.ToHashSet(StringComparer.Ordinal).SetEquals(TaskRetryEvidence.Selected(step, rule, taskRetry)))
                throw new IOException("configuration_run_tasks_unbound");
        }
        return step;
    }

    private void VerifyDefinitions(ExecutionPlan plan, ExecutionPlanStep step, ConfigurationRunRequest request)
    {
        var initialize = ProjectInitializationPlan.IsInitialization(plan);
        var catalog = new AutomationDefinitionStore(_database).Read();
        var binding = catalog.Bindings.SingleOrDefault(item => item.Id.ToString("D") == step.BindingId);
        var script = catalog.Scripts.SingleOrDefault(item => item.Id.ToString("D") == step.ScriptId);
        if (script is not null && binding is not null) script = BindingScriptSource.Resolve(script, binding);
        var user = catalog.Users.SingleOrDefault(item => item.Id.ToString("D") == step.UserId);
        if (binding is not null && user is not null) binding = AutomationBindingPolicy.Resolve(user, binding);
        if (binding is null || script is null || user is null || !binding.IsEnabled || binding.ScriptId != script.Id || binding.UserId != user.Id ||
            binding.Revision != request.ExpectedBindingRevision) throw new ConfigurationException("configuration_binding_revision_conflict");
        var root = catalog.Roots.SingleOrDefault(item => item.Id == script.RootId) ?? throw new ConfigurationException("configuration_run_root_missing");
        (string Kind, Guid Id, long Revision)[] expected = [("binding", binding.Id, binding.Revision), ("script", script.Id, script.Revision),
            ("user", user.Id, user.Revision), ("root", root.Id, root.Revision)];
        foreach (var item in expected)
            if (!plan.SourceRevisions.Any(value => value.EntityKind == item.Kind && value.EntityId == item.Id.ToString("D") && value.Revision == item.Revision))
                throw new ConfigurationException("configuration_run_definition_changed");
        var roots = catalog.Roots.ToDictionary(item => item.Id);
        string Resolve(AutomationPath path) => roots.TryGetValue(path.RootId, out var actual)
            ? PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(actual.Path, path.RelativePath))
            : throw new ConfigurationException("configuration_run_root_missing");
        if (plan.RootBindings.Any(item => !Guid.TryParse(item.RootId, out var id) || !roots.TryGetValue(id, out var actual) || actual.Path != item.Path ||
                !plan.SourceRevisions.Any(value => value.EntityKind == "root" && value.EntityId == item.RootId && value.Revision == actual.Revision)) ||
            step.UpdateConfigurationAtFinish != (!initialize && script.UpdateConfigurationAtFinish &&
                plan.Steps.Last(item => item.BindingId == step.BindingId).StepId == step.StepId) || !ExecutionPlanSources.MatchesScriptLaunch(step, Resolve(script.Executable), script.Arguments) || step.WorkingDirectory != root.Path ||
            !step.ConfigurationTargets.SequenceEqual(script.ConfigurationPaths.Select(Resolve)) || step.LogTargets.Count != (initialize ? 0 : script.LogPaths.Length) ||
            (script.Kind == AutomationKind.Generic ? step.EngineKind != "process" || step.Adapter is not null : step.ExtensionId != script.ExtensionId ||
                (script.AdapterResources.IsEmpty ? step.EngineKind != "extensionWorker" :
                    step.EngineKind is not ("process" or "extensionWorker") || step.Adapter is null)) ||
            script.Kind == AutomationKind.Specialized && !request.AutoSyncEnabled)
            throw new ConfigurationException("configuration_run_definition_changed");
        foreach (var (path, index) in script.LogPaths.Select((path, index) => (path, index)).Where(_ => !initialize))
            ExecutionLogSelectors.Require(step.LogTargets[index], index, ExecutionLogSelectors.Declaration(roots[path.RootId].Path, path.RelativePath));
    }

    private static void RequireSource(ConfigurationRevision source, ExecutionPlanStep step, ConfigurationTarget target)
    {
        if (source is null || source.Address != target.Address || source.Revision != target.ExpectedRevision || source.UserId.ToString("D") != step.UserId ||
            source.ScriptId.ToString("D") != step.ScriptId) throw new ConfigurationException("configuration_revision_conflict");
        ConfigurationRevisionPolicy.RequireCurrentContract(source, target.Contract);
    }

    private static ImmutableArray<ResourceClaim> Claims(ExecutionPlanStep step, ConfigurationRunRequest request, IEnumerable<ResourceClaim> physical) =>
        PhysicalResourceAdmission.Merge(physical.Concat(request.Resources.Select(item => new ResourceClaim(ConfigurationRevisionService.ResourceKey(item.Target.Address), ResourceClaimMode.Mutation)))
            .Concat([new("automation:definitions", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference),
                new($"configuration:script:{step.ScriptId}", ResourceClaimMode.Mutation)]));
}
