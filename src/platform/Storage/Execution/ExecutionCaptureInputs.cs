using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Maintenance;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class ExecutionCaptureInputs
{
    internal const int TimeoutMilliseconds = 5000;
    internal const long MaximumPngBytes = 8388608;
    internal const int MaximumBatchFrames = 200;
    internal const int MaximumCatalogEntries = 4096;
    internal const long MaximumBatchBytes = 67_108_864;
    internal const int BatchTimeoutMilliseconds = 30_000;
    internal const string Unbound = "execution_capture_runtime_unbound";
    private const string SingleFileHost = "singleFileHost";

    internal static bool FitsCatalog(long frames, int steps) => frames is >= 1 and <= MaximumBatchFrames &&
        steps >= 0 && frames * steps <= MaximumCatalogEntries;

    internal static bool IsSupported(ExecutionPlanStep? step) => step?.Target is
        { Kind: "pc", Pc: not null, Android: null } or { Kind: "android", Pc: null, Android.Driver: "genericAdb" or "mumu" };

    internal static ExecutionPlanCaptureRuntime? Capture(IReadOnlyList<ExecutionPlanStep> steps, ExecutionJudgementRuntime? runtime, PlanInputLease inputs)
    {
        if (runtime is null || !steps.Any(IsSupported)) return null;
        if (!FitsCatalog(runtime.CaptureFrameCount, steps.Count(IsSupported)) ||
            runtime.CaptureFrameCount > 1 && steps.Any(step => IsSupported(step) && step.Target?.Kind != "pc")) throw Invalid();
        var bundled = runtime.SingleFileCaptureHost;
        var result = new ExecutionPlanCaptureRuntime
        {
            Broker = ExecutionHookImages.Capture(runtime.BrokerExecutable, runtime: !bundled, inputs),
            BrokerDeployment = bundled ? SingleFileHost : null,
            TimeoutMilliseconds = runtime.CaptureFrameCount == 1 ? TimeoutMilliseconds : BatchTimeoutMilliseconds,
            MaximumPngBytes = MaximumPngBytes, MaximumFramesPerStep = runtime.CaptureFrameCount
        };
        RequireImage(result);
        if (bundled)
        {
            RequireCurrentHost(result);
            RequirePublishedImage(result.Broker);
        }
        return result;
    }

    internal static void Require(ExecutionPlan plan)
    {
        if (plan.CaptureRuntime is not { } runtime) return;
        if (!plan.Steps.Any(IsSupported) ||
            runtime.MaximumPngBytes != MaximumPngBytes ||
            !FitsCatalog(runtime.MaximumFramesPerStep, plan.Steps.Count(IsSupported)) ||
            runtime.TimeoutMilliseconds != (runtime.MaximumFramesPerStep == 1 ? TimeoutMilliseconds : BatchTimeoutMilliseconds) ||
            runtime.MaximumFramesPerStep > 1 && plan.Steps.Any(step => IsSupported(step) && step.Target?.Kind != "pc")) throw Invalid();
        RequireImage(runtime);
    }

    private static void RequireImage(ExecutionPlanCaptureRuntime runtime)
    {
        var image = runtime.Broker;
        ExecutionLaunchInputs.RequireImage(image);
        if (runtime.BrokerDeployment is not null)
        {
            if (runtime.BrokerDeployment != SingleFileHost || image.RuntimeFiles.Count != 0 ||
                Path.GetFileName(image.Path) != ApplicationTree.HostEntry) throw Invalid();
            return;
        }
        var stem = Path.GetFileNameWithoutExtension(image.Path);
        if (!Path.GetExtension(image.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            new[] { stem + ".deps.json", stem + ".runtimeconfig.json", stem + ".dll" }
                .Any(required => !image.RuntimeFiles.Any(file => file.RelativePath == required)) ||
            image.RuntimeFiles.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != image.RuntimeFiles.Count)
            throw Invalid();
    }

    internal static void RequireCurrentHost(ExecutionPlanCaptureRuntime runtime)
    {
        if (runtime.BrokerDeployment is null) return;
        if (runtime.BrokerDeployment != SingleFileHost || !IsSingleFileHost() ||
            !StringComparer.OrdinalIgnoreCase.Equals(runtime.Broker.Path, PhysicalPathPolicy.RuntimeProcessPath())) throw Invalid();
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "An empty entry assembly location identifies the running bundled Host.")]
    internal static bool IsSingleFileHost() =>
        System.Reflection.Assembly.GetEntryAssembly() is { Location.Length: 0 } entry && entry.GetName().Name == "NexusPipeline.Host";

    private static void RequirePublishedImage(ExecutionPlanInterpreterImage image)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(Path.Combine(Path.GetDirectoryName(image.Path)!, ApplicationTree.ManifestPath));
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > StrictJson.MaximumBytes) throw Invalid();
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        var host = ApplicationManifest.Read(new(), bytes, null).Files.Single(file => file.RelativePath == ApplicationTree.HostEntry);
        if (host.Sha256 != image.Sha256 || host.SizeBytes != image.SizeBytes) throw Invalid();
    }

    internal static IEnumerable<string> Paths(ExecutionPlan plan)
    {
        if (plan.CaptureRuntime is not { } runtime) yield break;
        yield return runtime.Broker.Path;
        var directory = Path.GetDirectoryName(runtime.Broker.Path)!;
        foreach (var file in runtime.Broker.RuntimeFiles) yield return Path.GetFullPath(Path.Combine(directory, file.RelativePath));
    }

    private static IOException Invalid() => new(Unbound);
}
