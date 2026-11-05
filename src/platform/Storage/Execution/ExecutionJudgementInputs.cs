using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class ExecutionJudgementInputs(RuntimeInstance instance, ContractReader contracts)
{
    internal static ConfigurationEditorCommand Command(ExecutionPlan total, string stepId)
    {
        var step = total.Steps.Single(item => item.StepId == stepId);
        var worker = step.JudgementWorker ?? throw new IOException("execution_judgement_worker_unbound");
        return EditorCommandPolicy.Freeze(new(worker.Broker.Path, worker.Broker.Sha256,
            ["--execution-judgement-worker"], step.WorkingDirectory));
    }

    internal JudgementWorkerInitialization ReadInitialization(ExecutionPlan total, string stepId,
        StepEvidenceBinding primary, string workerJob, Guid ownerId)
    {
        var step = total.Steps.Single(item => item.StepId == stepId);
        if (primary.InstanceId != instance.InstanceId || primary.RunId.ToString("D") != total.RunId || primary.StepId != stepId)
            throw new IOException("execution_judgement_binding_unbound");
        var frozen = step.JudgementWorker ?? throw new IOException("execution_judgement_worker_unbound");
        var script = ReadScript(total, stepId).Judgement;
        var initialization = new JudgementWorkerInitialization(JudgementWorkerProtocol.InitializationFormat, 1,
            new(primary, workerJob, ownerId), script.Language, script.Script!, frozen.ScriptSha256,
            ExecutionJudgementImages.FromWire(frozen.Broker), frozen.Python is null ? null : ExecutionJudgementImages.FromWire(frozen.Python),
            step.WorkingDirectory, FileTreeProtocol.FromWire(step.WorkingDirectoryIdentity),
            checked((int)total.Budget.JudgementTimeoutSeconds), checked((int)frozen.MaximumInvocations), frozen.MaximumEvidenceBytes);
        JudgementWorkerProtocol.Require(initialization);
        return initialization;
    }

    internal AutomationScriptScript ReadScript(ExecutionPlan total, string stepId)
        => ReadBundle(total).Scripts.Single(item => item.Id == total.Steps.Single(step => step.StepId == stepId).ScriptId);

    internal AutomationBundle ReadBundle(ExecutionPlan total)
    {
        var content = total.DefinitionBundle;
        if (content.SizeBytes is < 1 or > StrictJson.MaximumBytes) throw new IOException("execution_definition_reference_invalid");
        using var stream = new ImmutableBlobStore(instance, contracts).OpenVerified(new(content.RelativePath, content.SizeBytes, content.Sha256));
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        var bundle = contracts.Read<AutomationBundle>(bytes, "automation-bundle.schema.json");
        ExecutionPlanSources.Validate(total, bundle);
        return bundle;
    }
}
