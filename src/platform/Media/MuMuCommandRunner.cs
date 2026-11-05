using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Media;

internal sealed record MuMuCommandRun(MuMuCommandReply Reply, byte[] StandardOutput, byte[] StandardError);

[SupportedOSPlatform("windows")]
internal static class MuMuCommandRunner
{
    internal static async Task<MuMuCommandRun> RunAsync(MuMuCaptureRequest request, int sequence,
        Stream input, Stream channel, CancellationToken cancellation)
    {
        var image = sequence == 2 ? request.ScreenshotTool : request.Manager;
        string[] arguments = sequence == 2 ? ["-s", request.Endpoint, "exec-out", "screencap", "-p"] : ["info", "-v", "all"];
        var result = await OwnedCaptureCommand.RunAsync(image, arguments, request.Binding.Primary.InstanceId,
            request.Binding.Primary.AttemptId, sequence == 2 ? (int)ExecutionCaptureInputs.MaximumPngBytes : MuMuCaptureSequence.MaximumInfoBytes,
            sequence == 2 ? ExecutionCaptureInputs.MaximumPngBytes + MuMuCaptureSequence.MaximumErrorBytes : MuMuCaptureSequence.MaximumInfoBytes,
            request.StopTimeoutMilliseconds, (job, identity) =>
            {
                var process = new ProcessIdentity(identity.ProcessId, identity.CreatedAtUtc, identity.ImagePath,
                    identity.ImageSha256, identity.UserSid, identity.IsElevated);
                var ready = new MuMuChildReady(MuMuCaptureProtocol.ChildFormat, 1, request.Binding,
                    request.Source, sequence, MuMuCaptureSequence.Purpose(sequence), job.Name, process);
                MuMuCaptureProtocol.Require(ready, request, sequence);
                AdmitAsync(request, ready, input, channel, cancellation).GetAwaiter().GetResult();
            }, cancellation).ConfigureAwait(false);
        var natural = result.Natural; var exitCode = result.ExitCode;
        var outBytes = result.StandardOutput; var errorBytes = result.StandardError; var inventory = result.Stopped;
        var reply = new MuMuCommandReply(MuMuCaptureProtocol.CommandFormat, 1, request.Binding, request.Source, sequence,
            natural ? "natural-root-exit-and-eof-before-stop" : "unverified", exitCode,
            outBytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(outBytes)),
            errorBytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(errorBytes)), inventory);
        MuMuCaptureProtocol.Require(reply, request, sequence);
        await MuMuCaptureProtocol.WriteAsync(channel, reply, cancellation).ConfigureAwait(false);
        await MuMuCaptureProtocol.WriteBytesAsync(channel, outBytes, cancellation).ConfigureAwait(false);
        await MuMuCaptureProtocol.WriteBytesAsync(channel, errorBytes, cancellation).ConfigureAwait(false);
        return new(reply, outBytes, errorBytes);
    }

    private static async Task AdmitAsync(MuMuCaptureRequest request, MuMuChildReady ready,
        Stream input, Stream output, CancellationToken cancellation)
    {
        await MuMuCaptureProtocol.WriteAsync(output, ready, cancellation).ConfigureAwait(false);
        var admission = await MuMuCaptureProtocol.ReadAsync<MuMuChildAdmission>(input, cancellation).ConfigureAwait(false);
        if (admission.Format != MuMuCaptureProtocol.AdmissionFormat || admission.Version != 1 ||
            admission.Binding != request.Binding || admission.Source != request.Source || admission.Sequence != ready.Sequence ||
            admission.Process != ready.Process) throw MuMuCaptureProtocol.Invalid();
        StepEvidenceCodec.Plan(admission.RootArtifact); cancellation.ThrowIfCancellationRequested();
    }

}
