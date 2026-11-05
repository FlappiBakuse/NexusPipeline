using System.Collections.Immutable;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed record CaptureCommandRoot(string Format, int Version, ExecutionCaptureBinding Binding,
    BlobReference Source, BlobReference BrokerRoot, int Sequence, string Purpose, string JobName, ProcessIdentity Process);

internal sealed record ExecutionCaptureOutput(long SizeBytes, string Sha256, ImmutableArray<BlobReference> Parts);

internal sealed record CaptureCommandObserved(string Format, int Version, ExecutionCaptureBinding Binding,
    BlobReference CommandRoot, string Completion, int? ExitCode, ExecutionCaptureOutput StandardOutput,
    ExecutionCaptureOutput StandardError, OwnedTargetInventory Stopped);

internal sealed record CaptureCommandSaved(BlobReference RootArtifact, CaptureCommandRoot Root,
    BlobReference? ObservationArtifact, CaptureCommandObserved? Observation);

internal static class MuMuCaptureSequence
{
    internal const int MaximumCommands = 3, MaximumJobs = 4, MaximumEvents = 9;
    internal const int MaximumInfoBytes = 2097152, MaximumErrorBytes = 65536;
    private static readonly string[] Purposes = ["beforeMapping", "image", "afterMapping"];
    private static readonly string[] Stages = ["before-mapping-root", "before-mapping-observed",
        "image-root", "image-observed", "after-mapping-root", "after-mapping-observed"];

    internal static bool IsChild(string stage) => Array.IndexOf(Stages, stage) >= 0;
    internal static bool IsRoot(string stage) => IsChild(stage) && Array.IndexOf(Stages, stage) % 2 == 0;
    internal static string Stage(int sequence, bool observed) =>
        sequence is >= 1 and <= MaximumCommands ? Stages[(sequence - 1) * 2 + (observed ? 1 : 0)] : throw Invalid();
    internal static int Sequence(string stage) => IsChild(stage) ? Array.IndexOf(Stages, stage) / 2 + 1 : throw Invalid();
    internal static string Purpose(int sequence) => sequence is >= 1 and <= MaximumCommands ? Purposes[sequence - 1] : throw Invalid();
    internal static bool IsPaired(ExecutionPlanStep step) => step.Target?.Android is { Driver: "mumu", ScreenshotTool: not null };

    internal static ExecutionPlanInterpreterImage Image(ExecutionPlan total, ExecutionCaptureBinding binding, int sequence)
    {
        var target = total.Steps.Single(step => step.StepId == binding.Primary.StepId).Target?.Android ?? throw Invalid();
        if (target.Driver != "mumu" || target.ScreenshotTool is null || target.Mapping is null) throw Invalid();
        return sequence switch { 1 or 3 => target.Tool, 2 => target.ScreenshotTool, _ => throw Invalid() };
    }

    internal static void RequireOutput(ExecutionCaptureOutput value, int maximum, bool binary)
    {
        if (value is null || value.SizeBytes < 0 || value.SizeBytes > maximum || !FileTreePolicy.Digest(value.Sha256) ||
            value.Parts.IsDefault) throw Invalid();
        if (value.SizeBytes == 0)
        {
            if (!value.Parts.IsEmpty || value.Sha256 != Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>()))) throw Invalid();
        }
        else if (binary)
        {
            if (!value.Parts.IsEmpty) throw Invalid();
        }
        else JudgementFrameParts.Require(value.SizeBytes, value.Sha256, value.Parts, maximum);
    }

    internal static byte[] ReadOutput(ImmutableBlobStore blobs, ExecutionCaptureOutput value, int maximum)
    {
        RequireOutput(value, maximum, binary: false);
        return value.SizeBytes == 0 ? [] : JudgementFrameParts.Read(blobs, value.SizeBytes, value.Sha256, value.Parts, maximum);
    }

    internal static IOException Invalid() => new("execution_capture_mumu_unbound");
}
