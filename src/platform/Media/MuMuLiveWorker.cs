using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Media;

[SupportedOSPlatform("windows")]
public static class MuMuLiveWorker
{
    public static async Task<int> RunAsync()
    {
        try
        {
            using var input = Console.OpenStandardInput(); using var output = Console.OpenStandardOutput();
            var request = await MuMuCaptureProtocol.ReadAsync<MuMuLiveRequest>(input, default).ConfigureAwait(false);
            MuMuLiveProtocol.Require(request);
            if (ProcessIdentityReader.Read(Environment.ProcessId) != request.Broker) throw MuMuLiveProtocol.Invalid();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(request.TimeoutMilliseconds));
            var count = 0; var reason = "execution_capture_frame_verified";
            for (var sequence = 1; sequence <= 3; sequence++)
            {
                var current = sequence;
                var image = sequence == 2 ? request.ScreenshotTool : request.Manager;
                string[] args = sequence == 2 ? ["-s", request.Endpoint, "exec-out", "screencap", "-p"] : ["info", "-v", "all"];
                var result = await OwnedCaptureCommand.RunAsync(image, args, request.Binding.Primary.InstanceId,
                    request.Binding.Primary.AttemptId, sequence == 2 ? (int)ExecutionCaptureInputs.MaximumPngBytes : MuMuCaptureSequence.MaximumInfoBytes,
                    sequence == 2 ? ExecutionCaptureInputs.MaximumPngBytes + MuMuCaptureSequence.MaximumErrorBytes : MuMuCaptureSequence.MaximumInfoBytes,
                    request.StopTimeoutMilliseconds, (job, root) =>
                    {
                        var process = new ProcessIdentity(root.ProcessId, root.CreatedAtUtc, root.ImagePath, root.ImageSha256, root.UserSid, root.IsElevated);
                        var ready = new MuMuLiveReady(MuMuLiveProtocol.ReadyFormat, 1, request.Binding, current, job.Name, process);
                        MuMuLiveProtocol.Require(ready, request, current);
                        AdmitAsync(request, ready, input, output, deadline.Token).GetAwaiter().GetResult();
                    }, deadline.Token).ConfigureAwait(false);
                var reply = new MuMuLiveReply(MuMuLiveProtocol.ReplyFormat, 1, request.Binding, sequence,
                    result.Natural ? "natural-root-exit-and-eof-before-stop" : "unverified", result.ExitCode,
                    result.StandardOutput.LongLength, Convert.ToHexStringLower(SHA256.HashData(result.StandardOutput)),
                    result.StandardError.LongLength, Convert.ToHexStringLower(SHA256.HashData(result.StandardError)), result.Stopped);
                MuMuLiveProtocol.Require(reply, request, sequence);
                await MuMuCaptureProtocol.WriteAsync(output, reply, deadline.Token).ConfigureAwait(false);
                await MuMuCaptureProtocol.WriteBytesAsync(output, result.StandardOutput, deadline.Token).ConfigureAwait(false);
                await MuMuCaptureProtocol.WriteBytesAsync(output, result.StandardError, deadline.Token).ConfigureAwait(false);
                count++;
                if (!result.Natural || result.ExitCode != 0) { reason = "execution_capture_helper_failed"; break; }
                if (sequence != 2 && !MuMuLiveProtocol.Mapping(result.StandardOutput, result.StandardError, request))
                { reason = "execution_capture_mapping_unverified"; break; }
            }
            await MuMuCaptureProtocol.WriteAsync(output, new MuMuLiveCompleted(MuMuLiveProtocol.CompletedFormat,
                1, request.Binding, count, reason), deadline.Token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or ArgumentException or
            OperationCanceledException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { Console.Error.WriteLine("mumu_live_capture_worker_rejected"); return 1; }
    }

    private static async Task AdmitAsync(MuMuLiveRequest request, MuMuLiveReady ready,
        Stream input, Stream output, CancellationToken cancellation)
    {
        await MuMuCaptureProtocol.WriteAsync(output, ready, cancellation).ConfigureAwait(false);
        var admission = await MuMuCaptureProtocol.ReadAsync<MuMuLiveAdmission>(input, cancellation).ConfigureAwait(false);
        if (admission.Format != MuMuLiveProtocol.AdmissionFormat || admission.Version != 1 ||
            admission.Binding != request.Binding || admission.Sequence != ready.Sequence || admission.Process != ready.Process)
            throw MuMuLiveProtocol.Invalid();
        cancellation.ThrowIfCancellationRequested();
    }
}
