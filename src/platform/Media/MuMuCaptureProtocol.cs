using System.Text.Json;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Media;

internal sealed record MuMuCaptureRequest(string Format, int Version, ExecutionCaptureBinding Binding,
    BlobReference Source, BlobReference BrokerRoot, ProcessIdentity Broker, ExecutionPlanInterpreterImage Manager,
    ExecutionPlanInterpreterImage ScreenshotTool, long Index, string Endpoint, int TimeoutMilliseconds, int StopTimeoutMilliseconds);

internal sealed record MuMuChildReady(string Format, int Version, ExecutionCaptureBinding Binding,
    BlobReference Source, int Sequence, string Purpose, string JobName, ProcessIdentity Process);

internal sealed record MuMuChildAdmission(string Format, int Version, ExecutionCaptureBinding Binding,
    BlobReference Source, int Sequence, ProcessIdentity Process, BlobReference RootArtifact);

internal sealed record MuMuCommandReply(string Format, int Version, ExecutionCaptureBinding Binding,
    BlobReference Source, int Sequence, string Completion, int? ExitCode, long StandardOutputBytes, string StandardOutputSha256,
    long StandardErrorBytes, string StandardErrorSha256, OwnedTargetInventory Stopped);

internal sealed record MuMuCaptureCompleted(string Format, int Version, ExecutionCaptureBinding Binding,
    BlobReference Source, int Commands, string Outcome, string ReasonCode);

internal static class MuMuCaptureProtocol
{
    internal const string RequestFormat = "nexus.media.mumu-capture-request";
    internal const string ChildFormat = "nexus.media.mumu-capture-child-ready";
    internal const string AdmissionFormat = "nexus.media.mumu-capture-child-admission";
    internal const string CommandFormat = "nexus.media.mumu-capture-command";
    internal const string CompletedFormat = "nexus.media.mumu-capture-completed";
    internal const int MaximumChunkBytes = 1048576;

    internal static byte[] Encode<T>(T value) => BoundedJson.Serialize(value, StepResultCodec.Options);

    internal static T Decode<T>(ReadOnlyMemory<byte> bytes)
    {
        using var parsed = StrictJson.Parse(bytes);
        var value = parsed.RootElement.Deserialize<T>(StepResultCodec.Options) ?? throw Invalid();
        if (!JsonElement.DeepEquals(parsed.RootElement, JsonSerializer.SerializeToElement(value, StepResultCodec.Options))) throw Invalid();
        return value;
    }

    internal static void Require(MuMuCaptureRequest value)
    {
        if (value.Format != RequestFormat || value.Version != 1 || value.Binding?.Primary is null ||
            value.Binding.CaptureId == Guid.Empty || !ExecutionCaptureCodec.JobName(value.Binding.HelperJobName) ||
            value.TimeoutMilliseconds is < 1 or > ExecutionCaptureInputs.TimeoutMilliseconds ||
            value.StopTimeoutMilliseconds is < 1 or > 60000 || value.Index is < 0 or > int.MaxValue ||
            value.Endpoint != ExecutionAndroidCommands.Endpoint(value.Endpoint) ||
            !new Uri("http://" + value.Endpoint).IsLoopback || value.Manager is null || value.ScreenshotTool is null) throw Invalid();
        StepEvidenceCodec.Plan(value.Source); StepEvidenceCodec.Plan(value.BrokerRoot);
        TargetJobEvidence.RequireIdentity(value.Broker);
        ExecutionLaunchInputs.RequireImage(value.Manager); ExecutionLaunchInputs.RequireImage(value.ScreenshotTool);
    }

    internal static void Require(MuMuChildReady value, MuMuCaptureRequest request, int sequence)
    {
        if (value.Format != ChildFormat || value.Version != 1 || value.Binding != request.Binding ||
            value.Source != request.Source || value.Sequence != sequence ||
            value.Purpose != MuMuCaptureSequence.Purpose(sequence) ||
            !ExecutionCaptureCodec.JobName(value.JobName) || value.JobName == request.Binding.HelperJobName ||
            value.JobName == request.Binding.Primary.JobName) throw Invalid();
        TargetJobEvidence.RequireIdentity(value.Process);
        var image = sequence == 2 ? request.ScreenshotTool : request.Manager;
        if (!StringComparer.OrdinalIgnoreCase.Equals(value.Process.ImagePath, image.Path) ||
            value.Process.ImageSha256 != image.Sha256 || value.Process.UserSid != request.Broker.UserSid ||
            value.Process.IsElevated != request.Broker.IsElevated || value.Process == request.Broker) throw Invalid();
    }

    internal static void Require(MuMuCommandReply value, MuMuCaptureRequest request, int sequence)
    {
        if (value.Format != CommandFormat || value.Version != 1 || value.Binding != request.Binding ||
            value.Source != request.Source || value.Sequence != sequence ||
            value.Completion is not ("natural-root-exit-and-eof-before-stop" or "unverified") ||
            value.Completion == "natural-root-exit-and-eof-before-stop" && value.ExitCode is null ||
            value.StandardOutputBytes < 0 || value.StandardOutputBytes > (sequence == 2 ?
                ExecutionCaptureInputs.MaximumPngBytes : MuMuCaptureSequence.MaximumInfoBytes) ||
            value.StandardErrorBytes < 0 || value.StandardErrorBytes > MuMuCaptureSequence.MaximumErrorBytes ||
            sequence != 2 && value.StandardOutputBytes + value.StandardErrorBytes > MuMuCaptureSequence.MaximumInfoBytes ||
            !NexusPipeline.Core.Configuration.FileTreePolicy.Digest(value.StandardOutputSha256) ||
            !NexusPipeline.Core.Configuration.FileTreePolicy.Digest(value.StandardErrorSha256)) throw Invalid();
        TargetJobEvidence.RequireInventory(value.Stopped);
        if (value.Stopped.ActiveProcesses != 0 || value.Stopped.TotalProcesses == 0 ||
            !value.Stopped.LivingFamily.IsEmpty || value.Stopped.LimitFlags != 0x00002000) throw Invalid();
    }

    internal static Task WriteAsync<T>(Stream output, T value, CancellationToken cancellation) =>
        JudgementWorkerFraming.WriteAsync(output, Encode(value), cancellation);

    internal static async Task<T> ReadAsync<T>(Stream input, CancellationToken cancellation) =>
        Decode<T>(await JudgementWorkerFraming.ReadAsync(input, cancellation).ConfigureAwait(false) ?? throw Invalid());

    internal static async Task WriteBytesAsync(Stream output, ReadOnlyMemory<byte> bytes, CancellationToken cancellation)
    {
        for (var offset = 0; offset < bytes.Length; offset += MaximumChunkBytes)
            await JudgementWorkerFraming.WriteAsync(output, bytes.Slice(offset, Math.Min(MaximumChunkBytes, bytes.Length - offset)),
                cancellation, MaximumChunkBytes).ConfigureAwait(false);
    }

    internal static async Task ReadBytesAsync(Stream input, long size, int maximum,
        Func<ReadOnlyMemory<byte>, Task> consume, CancellationToken cancellation)
    {
        if (size < 0 || size > maximum) throw Invalid();
        for (long offset = 0; offset < size;)
        {
            var bytes = await JudgementWorkerFraming.ReadAsync(input, cancellation, MaximumChunkBytes).ConfigureAwait(false) ?? throw Invalid();
            if (bytes.Length != Math.Min(MaximumChunkBytes, size - offset)) throw Invalid();
            await consume(bytes).ConfigureAwait(false); offset += bytes.Length;
        }
    }

    internal static IOException Invalid() => new("execution_capture_mumu_protocol_invalid");
}
