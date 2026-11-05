using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Media;

internal sealed record MuMuLiveRequest(string Format, int Version, ExecutionCaptureBinding Binding,
    ProcessIdentity Broker, ExecutionPlanInterpreterImage Manager, ExecutionPlanInterpreterImage ScreenshotTool,
    long Index, string Endpoint, int TimeoutMilliseconds, int StopTimeoutMilliseconds);
internal sealed record MuMuLiveReady(string Format, int Version, ExecutionCaptureBinding Binding,
    int Sequence, string JobName, ProcessIdentity Process);
internal sealed record MuMuLiveAdmission(string Format, int Version, ExecutionCaptureBinding Binding,
    int Sequence, ProcessIdentity Process);
internal sealed record MuMuLiveReply(string Format, int Version, ExecutionCaptureBinding Binding,
    int Sequence, string Completion, int? ExitCode, long StandardOutputBytes, string StandardOutputSha256,
    long StandardErrorBytes, string StandardErrorSha256, OwnedTargetInventory Stopped);
internal sealed record MuMuLiveCompleted(string Format, int Version, ExecutionCaptureBinding Binding,
    int Commands, string ReasonCode);

internal static class MuMuLiveProtocol
{
    internal const string RequestFormat = "nexus.media.mumu-live-request";
    internal const string ReadyFormat = "nexus.media.mumu-live-child-ready";
    internal const string AdmissionFormat = "nexus.media.mumu-live-child-admission";
    internal const string ReplyFormat = "nexus.media.mumu-live-command";
    internal const string CompletedFormat = "nexus.media.mumu-live-completed";

    internal static void Require(MuMuLiveRequest value)
    {
        if (value.Format != RequestFormat || value.Version != 1 || value.Binding?.Primary is not { } primary ||
            primary.InstanceId == Guid.Empty || primary.RunId == Guid.Empty || primary.AttemptId == Guid.Empty ||
            primary.ParentOperationId == Guid.Empty || primary.ParentJournalId == Guid.Empty || primary.WriterId == Guid.Empty ||
            string.IsNullOrWhiteSpace(primary.StepId) || primary.StepId.Length > 80 || primary.SessionId < 0 ||
            !FileTreePolicy.Digest(primary.PlanDigest) || !ExecutionCaptureCodec.JobName(primary.JobName) ||
            value.Binding.CaptureId == Guid.Empty || !FileTreePolicy.Digest(value.Binding.TotalPlanSha256) ||
            !ExecutionCaptureCodec.JobName(value.Binding.HelperJobName) || value.Binding.HelperJobName == primary.JobName ||
            value.TimeoutMilliseconds is < 1 or > ExecutionCaptureInputs.TimeoutMilliseconds ||
            value.StopTimeoutMilliseconds is < 1 or > 60000 || value.Index is < 0 or > int.MaxValue ||
            value.Endpoint != ExecutionAndroidCommands.Endpoint(value.Endpoint) ||
            !new Uri("http://" + value.Endpoint).IsLoopback || value.Manager is null || value.ScreenshotTool is null) throw Invalid();
        TargetJobEvidence.RequireIdentity(value.Broker);
        ExecutionLaunchInputs.RequireImage(value.Manager); ExecutionLaunchInputs.RequireImage(value.ScreenshotTool);
    }

    internal static void Require(MuMuLiveReady value, MuMuLiveRequest request, int sequence)
    {
        if (value.Format != ReadyFormat || value.Version != 1 || value.Binding != request.Binding ||
            value.Sequence != sequence || sequence is < 1 or > 3 ||
            !ExecutionCaptureCodec.JobName(value.JobName) || value.JobName == request.Binding.HelperJobName ||
            value.JobName == request.Binding.Primary.JobName) throw Invalid();
        TargetJobEvidence.RequireIdentity(value.Process);
        var image = sequence == 2 ? request.ScreenshotTool : request.Manager;
        if (!StringComparer.OrdinalIgnoreCase.Equals(value.Process.ImagePath, image.Path) ||
            value.Process.ImageSha256 != image.Sha256 || value.Process.UserSid != request.Broker.UserSid ||
            value.Process.IsElevated != request.Broker.IsElevated || value.Process == request.Broker) throw Invalid();
    }

    internal static void Require(MuMuLiveReply value, MuMuLiveRequest request, int sequence)
    {
        if (value.Format != ReplyFormat || value.Version != 1 || value.Binding != request.Binding ||
            value.Sequence != sequence || value.Completion is not ("natural-root-exit-and-eof-before-stop" or "unverified") ||
            value.Completion == "natural-root-exit-and-eof-before-stop" && value.ExitCode is null ||
            value.StandardOutputBytes < 0 || value.StandardOutputBytes > (sequence == 2 ?
                ExecutionCaptureInputs.MaximumPngBytes : MuMuCaptureSequence.MaximumInfoBytes) ||
            value.StandardErrorBytes < 0 || value.StandardErrorBytes > MuMuCaptureSequence.MaximumErrorBytes ||
            sequence != 2 && value.StandardOutputBytes + value.StandardErrorBytes > MuMuCaptureSequence.MaximumInfoBytes ||
            !FileTreePolicy.Digest(value.StandardOutputSha256) || !FileTreePolicy.Digest(value.StandardErrorSha256)) throw Invalid();
        TargetJobEvidence.RequireInventory(value.Stopped);
        if (value.Stopped.ActiveProcesses != 0 || value.Stopped.TotalProcesses == 0 ||
            !value.Stopped.LivingFamily.IsEmpty || value.Stopped.LimitFlags != 0x00002000) throw Invalid();
    }

    internal static bool Mapping(byte[] stdout, byte[] stderr, MuMuLiveRequest request)
    {
        try
        {
            var utf8 = new System.Text.UTF8Encoding(false, true);
            if (!string.IsNullOrWhiteSpace(utf8.GetString(stderr))) return false;
            MuMuInfoParser.RequireCaptureMapping(utf8.GetString(stdout), request.Index, request.Endpoint);
            return true;
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or System.Text.DecoderFallbackException)
        { return false; }
    }

    internal static IOException Invalid() => new("mumu_live_capture_protocol_invalid");
}
