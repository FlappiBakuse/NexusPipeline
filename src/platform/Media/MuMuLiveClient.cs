using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.Platform.Media;

[SupportedOSPlatform("windows")]
internal sealed class MuMuLiveClient(OwnedWorkerJob helper, MuMuLiveRequest request, CapturePngSpool png) : IDisposable
{
    private readonly List<CaptureChildLease> _children = [];
    private bool _started, _disposed;

    internal async Task<MuMuLiveCompleted> RunAsync(Stream input, Stream output,
        Action<CaptureChildLease, Action> admit, CancellationToken cancellation)
    {
        if (_started || _disposed) throw MuMuLiveProtocol.Invalid();
        _started = true; MuMuLiveProtocol.Require(request);
        await MuMuCaptureProtocol.WriteAsync(output, request, cancellation).ConfigureAwait(false);
        var count = 0; var reason = "execution_capture_frame_verified";
        for (var sequence = 1; sequence <= 3; sequence++)
        {
            var ready = await MuMuCaptureProtocol.ReadAsync<MuMuLiveReady>(input, cancellation).ConfigureAwait(false);
            MuMuLiveProtocol.Require(ready, request, sequence);
            if (_children.Any(child => child.JobName == ready.JobName || child.ProcessIdentity == ready.Process)) throw MuMuLiveProtocol.Invalid();
            var child = new CaptureChildLease(helper, request.Binding, sequence, ready.JobName, ready.Process);
            _children.Add(child);
            admit(child, () => MuMuCaptureProtocol.WriteAsync(output,
                new MuMuLiveAdmission(MuMuLiveProtocol.AdmissionFormat, 1, request.Binding, ready.Sequence, ready.Process),
                cancellation).WaitAsync(TimeSpan.FromMilliseconds(request.TimeoutMilliseconds), cancellation).GetAwaiter().GetResult());
            var reply = await MuMuCaptureProtocol.ReadAsync<MuMuLiveReply>(input, cancellation).ConfigureAwait(false);
            MuMuLiveProtocol.Require(reply, request, sequence);
            byte[]? mapping = null;
            if (sequence == 2)
            {
                using var binary = new MuMuChunkStream(input, reply.StandardOutputBytes);
                await png.ReadOutputAsync(binary, cancellation).ConfigureAwait(false);
                if (binary.FinishHash() != reply.StandardOutputSha256) throw MuMuLiveProtocol.Invalid();
            }
            else mapping = await ReadAsync(input, reply.StandardOutputBytes, MuMuCaptureSequence.MaximumInfoBytes,
                reply.StandardOutputSha256, cancellation).ConfigureAwait(false);
            var error = await ReadAsync(input, reply.StandardErrorBytes, MuMuCaptureSequence.MaximumErrorBytes,
                reply.StandardErrorSha256, cancellation).ConfigureAwait(false);
            child.RequireObserved(reply.ExitCode, reply.Completion, reply.Stopped); count++;
            if (reply.Completion != "natural-root-exit-and-eof-before-stop" || reply.ExitCode != 0)
            { reason = "execution_capture_helper_failed"; break; }
            if (mapping is not null && !MuMuLiveProtocol.Mapping(mapping, error, request))
            { reason = "execution_capture_mapping_unverified"; break; }
        }
        var complete = await MuMuCaptureProtocol.ReadAsync<MuMuLiveCompleted>(input, cancellation).ConfigureAwait(false);
        if (complete.Format != MuMuLiveProtocol.CompletedFormat || complete.Version != 1 || complete.Binding != request.Binding ||
            complete.Commands != count || complete.ReasonCode != reason || reason == "execution_capture_frame_verified" && count != 3)
            throw MuMuLiveProtocol.Invalid();
        return complete;
    }

    private static async Task<byte[]> ReadAsync(Stream input, long size, int maximum, string hash, CancellationToken cancellation)
    {
        using var bytes = new MemoryStream();
        await MuMuCaptureProtocol.ReadBytesAsync(input, size, maximum,
            chunk => bytes.WriteAsync(chunk, cancellation).AsTask(), cancellation).ConfigureAwait(false);
        var result = bytes.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(result)) != hash) throw MuMuLiveProtocol.Invalid();
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var child in _children.AsEnumerable().Reverse()) child.Dispose();
    }
}
