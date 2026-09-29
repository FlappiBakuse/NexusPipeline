using System.Net;
using System.Text;
using System.Text.Json;
using NexusPipeline.Host.Composition;
using NexusPipeline.Platform.Networking;
using Xunit;

namespace NexusPipeline.Tests.Platform;

public sealed class OutboundHttpTests
{
    [Fact]
    public async Task InjectedTransportPreservesUriAndReadsCurrentProxyOptions()
    {
        var options = OutboundProxyOptions.Direct;
        List<(string Mode, OutboundHttpTarget Target, bool Redirect)> observed = [];
        var provider = new OutboundHttpClientProvider(() => options, (proxy, target, redirect) =>
        {
            observed.Add((proxy.Mode, target, redirect));
            return new EchoHandler();
        });
        using (HttpClient first = provider.CreateClient(new Uri("https://example.invalid/resource"), TimeSpan.FromSeconds(1)))
            Assert.Equal("https://example.invalid/resource", await first.GetStringAsync("https://example.invalid/resource"));
        options = new("http", "http://127.0.0.1:9090", "", "");
        using (HttpClient second = provider.CreateClient(OutboundHttpTarget.Loopback, TimeSpan.FromSeconds(2), true))
            Assert.Equal(TimeSpan.FromSeconds(2), second.Timeout);
        Assert.Equal(new[] { ("none", OutboundHttpTarget.External, false), ("http", OutboundHttpTarget.Loopback, true) }, observed);
    }

#if NEXUS_TEST_HOST
    [Fact]
    public async Task ControlledTransportEnforcesIdentityRequestsAndRestartReceipts()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "plan.json");
            string secret = "credential-canary-" + Guid.NewGuid().ToString("N");
            var plan = new TestHostTransport.Plan("owned-run", [
                new("first", "GET", "https://example.invalid/first", 200, Convert.ToBase64String(Encoding.UTF8.GetBytes("first-result")), "text/plain",
                    RequestHeaders: new() { ["Authorization"] = "Bearer " + secret }),
                new("second", "GET", "https://example.invalid/second", 409, "", "text/plain")]);
            File.WriteAllText(file, JsonSerializer.Serialize(plan));
            Assert.Throws<InvalidDataException>(() => TestHostTransport.Create(file, "foreign-run"));
            var provider = new OutboundHttpClientProvider(() => OutboundProxyOptions.Direct, TestHostTransport.Create(file, "owned-run"));
            using (var client = provider.CreateClient(OutboundHttpTarget.External, TimeSpan.FromSeconds(2)))
            {
                client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
                Assert.Equal("first-result", await client.GetStringAsync("https://example.invalid/first"));
            }
            var restarted = new OutboundHttpClientProvider(() => OutboundProxyOptions.Direct, TestHostTransport.Create(file, "owned-run"));
            using (var client = restarted.CreateClient(OutboundHttpTarget.External, TimeSpan.FromSeconds(2)))
            {
                using var response = await client.GetAsync("https://example.invalid/second");
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://example.invalid/unplanned"));
            }
            string receipts = File.ReadAllText(file + ".receipts.jsonl");
            Assert.DoesNotContain(secret, receipts);
            Assert.DoesNotContain("https://", receipts);
            Assert.Equal(3, File.ReadAllLines(file + ".receipts.jsonl").Length);
            Assert.Throws<InvalidDataException>(() => TestHostTransport.Create(file, "owned-run"));
        }
        finally { Directory.Delete(root, true); }
    }
#endif

    private sealed class EchoHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsoluteUri) });
    }
}
