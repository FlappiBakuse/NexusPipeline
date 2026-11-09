using System.Net;
using System.Runtime.Versioning;
using NexusPipeline.Platform.Execution.Judgement;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class JudgementHttpFixture : IDisposable
{
    private readonly PayloadHandler _handler;
    private readonly HttpClient _client;
    internal int Requests => Volatile.Read(ref _handler.Requests);

    internal JudgementHttpFixture(bool invalidUtf8 = false)
    {
        _handler = new(invalidUtf8 ? Enumerable.Repeat((byte)255, 2 * 1024 * 1024).ToArray() :
            Enumerable.Repeat((byte)'<', 2 * 1024 * 1024).ToArray());
        _client = new(_handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal Task<JudgementProbeReply> Observe(JudgementProbeRequest request, CancellationToken cancellation) =>
        JudgementNativeProbes.ObserveAsync(request, cancellation, _client);

    internal static string Script(string language, bool finalOnly = false, bool invalidUtf8 = false) => language == "javascript" ? $$"""
function nexusJudge(input) {
  {{(finalOnly ? "if (!input.isFinal) return {outcome:'unverified',reason:''};" : "")}}
  if (!Object.isFrozen(nexus.probes)) throw new Error('mutable probe API');
  const observation = nexus.probes.httpGet({url:'https://controlled.invalid/body',maxBytes:2097152});
  const expectedBody = '{{(invalidUtf8 ? "\\ufffd" : "<")}}'.repeat(2097152);
  if (!observation.ok || observation.status !== 200 || observation.body !== expectedBody || observation.truncated)
    throw new Error('saved HTTP observation mismatch');
  if (typeof nexus.probes.httpGet.GetType !== 'undefined') throw new Error('CLR exposed');
  return {outcome:'succeeded',reason:'controlled parent HTTP pipeline'};
}
""" : $$"""
def nexus_judge(input):
    {{(finalOnly ? "if not input['isFinal']: return {'outcome':'unverified','reason':''}" : "pass")}}
    try:
        nexus.probes.httpGet = None
    except AttributeError:
        pass
    else:
        raise AssertionError('mutable probe API')
    observation = nexus.probes.httpGet({'url':'https://controlled.invalid/body','maxBytes':2097152})
    expected_body = '{{(invalidUtf8 ? "\\ufffd" : "<")}}' * 2097152
    assert observation['ok'] and observation['status'] == 200 and observation['body'] == expected_body and not observation['truncated']
    return {'outcome':'succeeded','reason':'controlled parent HTTP pipeline'}
""";

    private sealed class PayloadHandler(byte[] bytes) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Requests);
            if (request.Method != HttpMethod.Get || request.RequestUri?.AbsoluteUri != "https://controlled.invalid/body")
                throw new IOException("controlled_http_request_unbound");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request });
        }
    }

    public void Dispose() => _client.Dispose();
}
