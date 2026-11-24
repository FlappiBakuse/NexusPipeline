using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Execution.Judgement;

[SupportedOSPlatform("windows")]
internal sealed class JudgementProbeBridge(JudgementWorkerInvocation invocation, Stream input, Stream output, CancellationToken cancellation) : IDisposable
{
    private readonly IncrementalHash _transcript = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _encodedBytes;
    internal int ProbeCount { get; private set; }

    internal JsonElement Call(string method, JsonElement options)
    {
        var request = new JudgementProbeRequest(JudgementProbeProtocol.RequestFormat, 1, invocation.Identity,
            invocation.Generation, invocation.InputSha256, ProbeCount + 1, method, options.Clone());
        var reply = ForwardAsync(request).GetAwaiter().GetResult();
        if (reply.FaultCode is { } fault) throw new IOException(fault);
        return reply.Result!.Value.Clone();
    }

    internal async Task<JudgementProbeReply> ForwardAsync(JudgementProbeRequest request)
    {
        cancellation.ThrowIfCancellationRequested();
        JudgementProbeProtocol.Require(request, invocation, ProbeCount + 1);
        await JudgementWorkerHost.WriteAsync(output, request, cancellation).ConfigureAwait(false);
        var bytes = await JudgementWorkerFraming.ReadAsync(input, cancellation, JudgementProbeProtocol.MaximumFrameBytes).ConfigureAwait(false)
            ?? throw new IOException("judgement_probe_reply_missing");
        var reply = JudgementWorkerProtocol.Decode<JudgementProbeReply>(bytes);
        JudgementProbeProtocol.Require(reply, request);
        using var frame = new JudgementProbeFrame(new(request, reply));
        // The received exchange remains in the transcript if the cumulative allowance faults the call.
        _transcript.AppendData(frame.Bytes.Span);
        _encodedBytes = checked(_encodedBytes + frame.Bytes.Length);
        ProbeCount++;
        if (_encodedBytes > invocation.ProbeEvidenceAllowance) throw new IOException("judgement_probe_protocol_unrecognized");
        cancellation.ThrowIfCancellationRequested();
        return reply;
    }

    internal string FinishTranscript() => Convert.ToHexStringLower(_transcript.GetHashAndReset());

    public void Dispose() => _transcript.Dispose();
}
