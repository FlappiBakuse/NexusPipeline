using System.Text.Json;
using System.Runtime.Versioning;

namespace NexusPipeline.Platform.Execution.Judgement;

[SupportedOSPlatform("windows")]
internal sealed class JudgementProbeBridge(JudgementWorkerInvocation invocation, Stream input, Stream output, CancellationToken cancellation)
{
    private readonly List<JudgementProbeExchange> _exchanges = [];
    internal IReadOnlyList<JudgementProbeExchange> Exchanges => _exchanges.ToArray();

    internal JsonElement Call(string method, JsonElement options)
    {
        var request = new JudgementProbeRequest(JudgementProbeProtocol.RequestFormat, 1, invocation.Identity,
            invocation.Generation, invocation.InputSha256, _exchanges.Count + 1, method, options.Clone());
        var reply = ForwardAsync(request).GetAwaiter().GetResult();
        if (reply.FaultCode is { } fault) throw new IOException(fault);
        return reply.Result!.Value.Clone();
    }

    internal async Task<JudgementProbeReply> ForwardAsync(JudgementProbeRequest request)
    {
        cancellation.ThrowIfCancellationRequested();
        JudgementProbeProtocol.Require(request, invocation, _exchanges.Count + 1);
        await JudgementWorkerHost.WriteAsync(output, request, cancellation).ConfigureAwait(false);
        var bytes = await JudgementWorkerFraming.ReadAsync(input, cancellation, JudgementProbeProtocol.MaximumFrameBytes).ConfigureAwait(false)
            ?? throw new IOException("judgement_probe_reply_missing");
        var reply = JudgementWorkerProtocol.Decode<JudgementProbeReply>(bytes);
        JudgementProbeProtocol.Require(reply, request);
        _exchanges.Add(new(request, reply));
        JudgementProbeProtocol.RequireExchanges(_exchanges, invocation);
        cancellation.ThrowIfCancellationRequested();
        return reply;
    }
}
