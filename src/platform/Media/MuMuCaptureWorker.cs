using System.Runtime.Versioning;
using System.Text;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Media;

[SupportedOSPlatform("windows")]
public static class MuMuCaptureWorker
{
    public static async Task<int> RunAsync()
    {
        try
        {
            using var input = Console.OpenStandardInput(); using var output = Console.OpenStandardOutput();
            var request = await MuMuCaptureProtocol.ReadAsync<MuMuCaptureRequest>(input, default).ConfigureAwait(false);
            MuMuCaptureProtocol.Require(request);
            if (ProcessIdentityReader.Read(Environment.ProcessId) != request.Broker) throw MuMuCaptureProtocol.Invalid();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(request.TimeoutMilliseconds));
            var count = 0; var outcome = "completed"; var reason = "execution_capture_frame_verified";
            for (var sequence = 1; sequence <= MuMuCaptureSequence.MaximumCommands; sequence++)
            {
                var command = await MuMuCommandRunner.RunAsync(request, sequence, input, output, deadline.Token).ConfigureAwait(false);
                count++;
                if (command.Reply.Completion != "natural-root-exit-and-eof-before-stop" || command.Reply.ExitCode != 0)
                { outcome = "unavailable"; reason = "execution_capture_helper_failed"; break; }
                if (sequence != 2)
                {
                    try
                    {
                        var utf8 = new UTF8Encoding(false, true);
                        if (!string.IsNullOrWhiteSpace(utf8.GetString(command.StandardError))) throw MuMuCaptureProtocol.Invalid();
                        MuMuInfoParser.RequireCaptureMapping(utf8.GetString(command.StandardOutput), request.Index, request.Endpoint);
                    }
                    catch (Exception error) when (error is IOException or System.Text.Json.JsonException or DecoderFallbackException)
                    { outcome = "unavailable"; reason = "execution_capture_mapping_unverified"; break; }
                }
            }
            deadline.Token.ThrowIfCancellationRequested();
            await MuMuCaptureProtocol.WriteAsync(output, new MuMuCaptureCompleted(MuMuCaptureProtocol.CompletedFormat, 1,
                request.Binding, request.Source, count, outcome, reason), deadline.Token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or ArgumentException or
            OperationCanceledException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { Console.Error.WriteLine("execution_capture_mumu_worker_rejected"); return 1; }
    }
}
