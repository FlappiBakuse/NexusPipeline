using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Networking;
using NexusPipeline.TestFixtures;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class HttpProbeTests
{
    [Theory(DisplayName = "Actual Http Responses Decode Utf 8 And Compression Before Applying Their Body Limit")]
    [InlineData("identity")]
    [InlineData("gzip")]
    [InlineData("deflate")]
    public async Task DecodesResponseBodies(string encoding)
    {
        var body = Encoding.UTF8.GetBytes("HTTP 原生响应 & <body>");
        var encoded = Compress(body, encoding);
        await using var server = new NativeHttpFixture(_ => new(200, encoded,
            encoding == "identity" ? null : new Dictionary<string, string> { ["Content-Encoding"] = encoding }));
        using var client = ProbeHttpClient.Create(new(ProxyMode.None, null, ""), null);
        var result = await Get(server.Address, client);
        Assert.True(result.Ok); Assert.Equal(200, result.Status); Assert.Null(result.Error);
        Assert.False(result.Truncated); Assert.Equal(Encoding.UTF8.GetString(body), result.Body);
        Assert.Single(server.Requests);
    }

    [Theory(DisplayName = "A Real Compressed Body Is Bounded By Decoded Bytes And Only The Extra Byte Marks Truncation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundsDecodedBodies(bool extra)
    {
        var body = Enumerable.Repeat((byte)0xff, HttpGetProbe.MaximumBodyBytes + (extra ? 1 : 0)).ToArray();
        await using var server = new NativeHttpFixture(_ => new(200, Compress(body, "gzip"),
            new Dictionary<string, string> { ["Content-Encoding"] = "gzip" }));
        using var client = ProbeHttpClient.Create(new(ProxyMode.None, null, ""), null);
        var result = await Get(server.Address, client);
        Assert.True(result.Ok); Assert.Equal(extra, result.Truncated);
        Assert.Equal(HttpGetProbe.MaximumBodyBytes, result.Body.Length);
        Assert.All(result.Body, character => Assert.Equal('\ufffd', character));
    }

    [Fact(DisplayName = "A Non Success Http Status Retains Its Actual Body Without Becoming A Transport Failure")]
    public async Task PreservesHttpFailures()
    {
        await using var server = new NativeHttpFixture(_ => new(404, Encoding.UTF8.GetBytes("missing resource")));
        using var client = ProbeHttpClient.Create(new(ProxyMode.None, null, ""), null);
        var result = await Get(server.Address, client);
        Assert.False(result.Ok); Assert.Equal(404, result.Status); Assert.Equal("missing resource", result.Body); Assert.Null(result.Error);
    }

    [Theory(DisplayName = "Actual Redirects Use The Bounded Redirect Policy")]
    [InlineData(50)]
    [InlineData(51)]
    public async Task BoundsActualRedirects(int redirects)
    {
        await using var server = new NativeHttpFixture(request =>
        {
            var hop = int.Parse(request.Target.Trim('/'));
            return hop < redirects ? new(302, [], new Dictionary<string, string> { ["Location"] = "/" + (hop + 1) }) : new(200, Encoding.UTF8.GetBytes("redirect complete"));
        });
        using var client = ProbeHttpClient.Create(new(ProxyMode.None, null, ""), null);
        var result = await Get(new(server.Address, "0"), client);
        Assert.Equal(redirects == 50, result.Ok);
        if (redirects == 50) Assert.Equal("redirect complete", result.Body);
        else Assert.True(result.Error == "network_error" || result.Status == 302);
        Assert.Equal(51, server.Requests.Count);
    }

    [Fact(DisplayName = "A Real Self Signed Tls Endpoint Is Rejected Without A Trust Override")]
    public async Task RejectsUntrustedTls()
    {
        await using var server = new NativeHttpFixture(_ => new(200, Encoding.UTF8.GetBytes("must not be accepted")), tls: true);
        using var client = ProbeHttpClient.Create(new(ProxyMode.None, null, ""), null);
        var result = await Get(server.Address, client);
        Assert.False(result.Ok); Assert.Equal("network_error", result.Error); Assert.Equal("", result.Body);
        Assert.Empty(server.Requests);
    }

    [Fact(DisplayName = "A Redirect Retains Only Its Own Http Cookie Jar")]
    public async Task IsolatesRedirectCookies()
    {
        await using var server = new NativeHttpFixture(request => request.Target == "/start"
            ? new(302, [], new Dictionary<string, string> { ["Location"] = "/finish", ["Set-Cookie"] = "nexus-fixture=value; Path=/" })
            : new(200, Encoding.UTF8.GetBytes(request.Headers.GetValueOrDefault("Cookie", "missing"))));
        using var client = ProbeHttpClient.Create(new(ProxyMode.None, null, ""), null);
        var result = await Get(new(server.Address, "start"), client);
        Assert.True(result.Ok); Assert.Equal("nexus-fixture=value", result.Body); Assert.Equal(2, server.Requests.Count);
    }

    [Fact(DisplayName = "A Real Slow Response Produces A Bounded Timeout With No Exception Text")]
    public async Task BoundsSlowResponses()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new NativeHttpFixture(async (_, cancellation) =>
        {
            entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellation); return new(200, []);
        });
        using var client = ProbeHttpClient.Create(new(ProxyMode.None, null, ""), null);
        var running = Get(server.Address, client, timeout: 1000);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Ok); Assert.Equal("timeout", result.Error); Assert.Equal(0, result.Status); Assert.Equal("", result.Body);
    }

    [Fact(DisplayName = "Parent Cancellation Interrupts An Actual Pending Request Instead Of Returning A Fake Observation")]
    public async Task CancelsPendingRequests()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new NativeHttpFixture(async (_, cancellation) =>
        {
            entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellation); return new(200, []);
        });
        using var client = ProbeHttpClient.Create(new(ProxyMode.None, null, ""), null);
        using var cancellation = new CancellationTokenSource();
        var running = Get(server.Address, client, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory(DisplayName = "An Actual Custom Proxy Authenticates Without Sending Its Password To The Origin")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectsProxyCredentials(bool configuredPassword)
    {
        const string user = "proxy-fixture-user", password = "proxy-fixture-canary";
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + (configuredPassword ? password : "")));
        await using var origin = new NativeHttpFixture(_ => new(200, Encoding.UTF8.GetBytes("origin")));
        await using var proxy = new NativeHttpFixture(request =>
            !request.Headers.TryGetValue("Proxy-Authorization", out var authorization) || authorization != expected
                ? new(407, [], new Dictionary<string, string> { ["Proxy-Authenticate"] = "Basic realm=fixture" })
                : new(302, [], new Dictionary<string, string> { ["Location"] = origin.Address.ToString() }));
        using var secret = configuredPassword ? new SecretValue(password) : null;
        using var client = ProbeHttpClient.Create(new(ProxyMode.Http, proxy.Address.ToString(), user), secret);
        var result = await Get(new("http://nexus-proxy-fixture.invalid/start"), client);
        Assert.True(result.Ok); Assert.Equal("origin", result.Body);
        Assert.Contains(proxy.Requests, request => request.Headers.ContainsKey("Proxy-Authorization"));
        var destination = Assert.Single(origin.Requests);
        Assert.False(destination.Headers.ContainsKey("Proxy-Authorization")); Assert.False(destination.Headers.ContainsKey("Authorization"));
    }

    [Fact(DisplayName = "Loopback Requests Bypass An Actual Configured Proxy And Never Expose Its Credential")]
    public async Task BypassesLoopbackProxy()
    {
        await using var origin = new NativeHttpFixture(_ => new(200, Encoding.UTF8.GetBytes("direct")));
        await using var proxy = new NativeHttpFixture(_ => new(502, []));
        using var secret = new SecretValue("loopback-proxy-fixture-canary");
        using var client = ProbeHttpClient.Create(new(ProxyMode.Http, proxy.Address.ToString(), "user"), secret);
        var result = await Get(origin.Address, client);
        Assert.True(result.Ok); Assert.Equal("direct", result.Body); Assert.Empty(proxy.Requests);
        Assert.False(Assert.Single(origin.Requests).Headers.ContainsKey("Proxy-Authorization"));
    }

    [Theory(DisplayName = "The Process System Proxy Is Used Only By System Mode And Always Bypasses Loopback")]
    [InlineData(ProxyMode.None)]
    [InlineData(ProxyMode.System)]
    public async Task HonorsProxyMode(ProxyMode mode)
    {
        await using var origin = new NativeHttpFixture(_ => new(200, Encoding.UTF8.GetBytes("direct")));
        await using var proxy = new NativeHttpFixture(_ => new(200, Encoding.UTF8.GetBytes("system proxy")));
        var previous = HttpClient.DefaultProxy;
        try
        {
            HttpClient.DefaultProxy = new ForcedProxy(proxy.Address);
            using var client = ProbeHttpClient.Create(new(mode, null, ""), null);
            var direct = await Get(origin.Address, client);
            Assert.True(direct.Ok); Assert.Equal("direct", direct.Body); Assert.Empty(proxy.Requests);
            if (mode == ProxyMode.System)
            {
                var remote = await Get(new("http://nexus-system-proxy-fixture.invalid/path"), client);
                Assert.True(remote.Ok); Assert.Equal("system proxy", remote.Body); Assert.Single(proxy.Requests);
            }
        }
        finally { HttpClient.DefaultProxy = previous; }
    }

    private sealed class ForcedProxy(Uri address) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => address;
        public bool IsBypassed(Uri host) => false;
    }

    private static Task<HttpGetObservation> Get(Uri address, HttpClient client, CancellationToken cancellation = default, int timeout = 15000)
        => HttpGetProbe.CaptureAsync(HttpGetProbe.Read(JsonSerializer.SerializeToElement(new { url = address.ToString(), timeoutMs = timeout })), client, cancellation);

    private static byte[] Compress(byte[] bytes, string encoding)
    {
        if (encoding == "identity") return bytes;
        using var output = new MemoryStream();
        using (Stream compression = encoding == "gzip" ? new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true) :
                   new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true)) compression.Write(bytes);
        return output.ToArray();
    }
}
