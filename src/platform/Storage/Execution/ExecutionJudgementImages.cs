using NexusPipeline.Core.Automation;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Execution.Judgement;

namespace NexusPipeline.Platform.Storage.Execution;

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
