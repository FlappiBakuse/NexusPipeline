using NexusPipeline.Extensibility.Protocol;
using System.Collections.Immutable;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class RunConfigurationBinding
{
    internal static void Require(FrozenRunState state, string stepId, string targetPath, RunAttemptPlan attempt, ExecutionPlan total, ImmutableBlobStore? blobs = null, ContractReader? contracts = null, bool laterInvocation = false, ExecutionTaskRetry? taskRetry = null)
    {
        var step = total.Steps.SingleOrDefault(item => item.StepId == stepId)
            ?? throw new IOException("run_attempt_configuration_step_unknown");
        var revision = total.SourceRevisions.SingleOrDefault(item => item.EntityKind == "binding" && item.EntityId == step.BindingId);
        if (state.OwnerOperationId.ToString("D") != attempt.OperationId || state.Source.Address.BindingId.ToString("D") != step.BindingId ||
            state.Source.UserId.ToString("D") != step.UserId || state.Source.ScriptId.ToString("D") != step.ScriptId ||
            revision is null || state.BindingRevision != revision.Revision ||
            attempt.AttemptOrdinal == 1 && !laterInvocation && state.InitialPublicationRevision != state.Source.Revision || state.SyncInitially && (attempt.AttemptOrdinal != 1 || laterInvocation) ||
            state.AutoSyncEnabled && state.SyncAtFinish != step.UpdateConfigurationAtFinish ||
            step.EngineKind == "extensionWorker" && (!state.AutoSyncEnabled ||
                state.SyncAtFinish != step.UpdateConfigurationAtFinish))
            throw new IOException("run_attempt_configuration_owner_unbound");
        PlanConfigurationSlots.Require(total, stepId, targetPath, state.Source);
        if (step.Provider is not null && step.Adapter is null)
            ProviderConfigurationPolicy.RequireFrozenSource(total, step, state.Source,
                blobs ?? throw new IOException("configuration_provider_descriptor_required"),
                contracts ?? throw new IOException("configuration_provider_descriptor_required"));
        if (step.Adapter is not null) ExecutionAdapterBinding.RequireFrozenConfiguration(total, step, state.Source,
            blobs ?? throw new IOException("configuration_adapter_descriptor_required"), contracts ?? throw new IOException("configuration_adapter_descriptor_required"));
        var taskIds = step.Tasks.Select(item => item.TaskId).ToHashSet(StringComparer.Ordinal);
        ExecutionTaskProjection.RequireRules(step, state.Source.Address.ResourceId, state.Tasks.Select(item => item.Rule).ToImmutableArray());
        ExecutionTaskProjection.RequireValues(step, state.Tasks);
        if (step.Tasks.Any(task => task.Projection?.ResourceId == state.Source.Address.ResourceId))
            ExecutionTaskProjection.RequireBoundRules(total, step, state.Source, state.Tasks.Select(item => item.Rule).ToImmutableArray(),
                blobs ?? throw new IOException("configuration_adapter_descriptor_required"), contracts ?? throw new IOException("configuration_adapter_descriptor_required"));
        foreach (var captured in state.Tasks)
        {
            var declared = captured.Rule.Declaration.AuthorizedIds.ToHashSet(StringComparer.Ordinal);
            if (!declared.IsSubsetOf(taskIds) || !captured.Rule.SelectedIds.ToHashSet(StringComparer.Ordinal)
                .SetEquals(TaskRetryEvidence.Selected(step, captured.Rule, taskRetry)))
                throw new IOException("run_attempt_configuration_tasks_unbound");
        }
    }
}
