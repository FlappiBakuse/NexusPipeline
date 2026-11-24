using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;

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
        if (JournalReadScope.ReadDefinitions(instance, total) is { } owned)
        {
            ExecutionPlanSources.Validate(total, owned);
            return owned;
        }
        using var stream = new ImmutableBlobStore(instance, contracts).OpenVerified(new(content.RelativePath, content.SizeBytes, content.Sha256));
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        var bundle = contracts.Read<AutomationBundle>(bytes, "automation-bundle.schema.json");
        ExecutionPlanSources.Validate(total, bundle);
        return bundle;
    }
}

public sealed record ExecutionJudgementRuntime(string BrokerExecutable, string? PythonExecutable)
{
    public int CaptureFrameCount { get; init; } = 1;
    internal bool SingleFileCaptureHost { get; init; }
}

internal static class ExecutionJudgementImages
{
    internal static ExecutionPlanJudgementWorker? Capture(AutomationJudgement judgement, ExecutionJudgementRuntime? runtime, PlanInputLease inputs)
    {
        if (judgement.Language == AutomationJudgementLanguage.Keywords) return null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (runtime is null || judgement.Script is null) throw new IOException("execution_judgement_runtime_unavailable");
        var broker = ReadInterpreter(runtime.BrokerExecutable, inputs);
        if (!broker.RuntimeFiles.Any(file => file.RelativePath == Path.GetFileNameWithoutExtension(broker.Path) + ".deps.json"))
            throw new IOException("execution_judgement_broker_assets_missing");
        return new()
        {
            Broker = broker, Python = judgement.Language == AutomationJudgementLanguage.Python
                ? ReadInterpreter(runtime.PythonExecutable ?? throw new IOException("execution_judgement_python_unavailable"), inputs) : null,
            ScriptSha256 = JudgementWorkerProtocol.Hash(JudgementWorkerProtocol.Utf8.GetBytes(judgement.Script)),
            MaximumInvocations = 4095, MaximumEvidenceBytes = 64L * 1024 * 1024
        };
    }

    private static ExecutionPlanInterpreterImage ReadInterpreter(string requested, PlanInputLease inputs)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(requested);
        var image = inputs.ReadImage(path, "judgement_interpreter_size_invalid");
        return image with { RuntimeFiles = ExecutionHookImages.RuntimeFiles(path, inputs).ToArray() };
    }

    internal static JudgementInterpreterImage FromWire(ExecutionPlanInterpreterImage image) => new(image.Path, image.SizeBytes, image.Sha256,
        FileTreeProtocol.FromWire(image.Identity), FileTreeProtocol.FromWire(image.ParentIdentity), image.RuntimeFiles.Select(file =>
            new JudgementRuntimeFile(file.RelativePath, file.SizeBytes, file.Sha256,
                FileTreeProtocol.FromWire(file.Identity), FileTreeProtocol.FromWire(file.ParentIdentity))).ToArray());

    internal static IEnumerable<(string Path, long Size, string Sha256, FileTreePhysicalIdentity Identity, FileTreePhysicalIdentity Parent)> Files(ExecutionPlanStep step)
    {
        if (step.JudgementWorker is not { } worker) yield break;
        foreach (var image in new[] { worker.Broker, worker.Python }.OfType<ExecutionPlanInterpreterImage>())
        {
            yield return (image.Path, image.SizeBytes, image.Sha256, image.Identity, image.ParentIdentity);
            foreach (var file in image.RuntimeFiles)
                yield return (Path.GetFullPath(Path.Combine(Path.GetDirectoryName(image.Path)!, file.RelativePath)), file.SizeBytes, file.Sha256, file.Identity, file.ParentIdentity);
        }
    }

}
