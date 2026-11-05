using System.Net;
using System.Security.Cryptography;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.TestFixtures;
using Xunit;

namespace NexusPipeline.StorageTests;

[Collection("SerialNative")]
public sealed class StoreTransferTests
{
    [Theory(DisplayName = "The Actual Store Client Returns Every Redirect Before Contacting Its Destination")]
    [InlineData(301)] [InlineData(302)] [InlineData(303)] [InlineData(307)] [InlineData(308)]
    public async Task ReturnsUnfollowedRedirects(int status)
    {
        await using var destination = new NativeHttpFixture(_ => new NativeHttpReply(200, "untrusted"u8.ToArray()));
        await using var source = new NativeHttpFixture(_ => new NativeHttpReply(status, [],
            new Dictionary<string, string> { ["Location"] = destination.Address.AbsoluteUri }));
        using var client = Client();
        using var response = await client.GetAsync(source.Address, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Single(source.Requests); Assert.Empty(destination.Requests);
        await Assert.ThrowsAsync<IOException>(() => StoreResponseReader.Metadata(response, default));
    }

    [Fact(DisplayName = "The Actual Store Client Neither Reuses Response Cookies Nor Transforms Encoded Signed Bytes")]
    public async Task PreservesSignedBytes()
    {
        var original = new byte[] { 0x1f, 0x8b, 0x00, 0x01, 0x02 };
        await using var server = new NativeHttpFixture(_ => new NativeHttpReply(200, original,
            new Dictionary<string, string> { ["Set-Cookie"] = "session=fixture; Path=/", ["Content-Encoding"] = "gzip" }));
        using var client = Client();
        using var first = await client.GetAsync(server.Address, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(original, await first.Content.ReadAsByteArrayAsync());
        Assert.Contains("gzip", first.Content.Headers.ContentEncoding);
        using var second = await client.GetAsync(server.Address, HttpCompletionOption.ResponseHeadersRead);
        await Assert.ThrowsAsync<IOException>(() => StoreResponseReader.Metadata(second, default));
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, request => Assert.False(request.Headers.ContainsKey("Cookie")));
    }

    [Theory(DisplayName = "Actual Length Delimited And Chunked Metadata Preserve Every Original Byte")]
    [InlineData(false)] [InlineData(true)]
    public async Task PreservesOriginalMetadata(bool chunked)
    {
        var original = " \r\n{\"format\":\"fixture\",\"value\":\"原字节\"}\n "u8.ToArray();
        await using var server = new StoreTransferFixture(chunked ? "" : $"Content-Length: {original.Length}\r\n", original, chunked);
        using var client = Client();
        using var response = await client.GetAsync(server.Address, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(original, await StoreResponseReader.Metadata(response, default));
    }

    [Theory(DisplayName = "Actual Metadata Responses Cannot Bypass The Raw Size Encoding And Completeness Boundary")]
    [InlineData("empty")] [InlineData("encoded")] [InlineData("range")]
    [InlineData("declared-overflow")] [InlineData("truncated")] [InlineData("chunked-overflow")]
    public async Task BoundsMetadataResponses(string fault)
    {
        var body = fault == "chunked-overflow" ? new byte[StrictJson.MaximumBytes + 1] : "{}"u8.ToArray();
        var headers = fault switch
        {
            "empty" => "Content-Length: 0\r\n",
            "encoded" => "Content-Encoding: gzip\r\nContent-Length: 2\r\n",
            "range" => "Content-Range: bytes 0-1/2\r\nContent-Length: 2\r\n",
            "declared-overflow" => $"Content-Length: {StrictJson.MaximumBytes + 1}\r\n",
            "truncated" => "Content-Length: 20\r\n",
            _ => ""
        };
        if (fault == "empty") body = [];
        await using var server = new StoreTransferFixture(headers, body, fault == "chunked-overflow");
        using var client = Client();
        using var response = await client.GetAsync(server.Address, HttpCompletionOption.ResponseHeadersRead);
        await Assert.ThrowsAnyAsync<IOException>(() => StoreResponseReader.Metadata(response, default));
    }

    [Theory(DisplayName = "Actual Artifact Transfers Verify Length And Digest Without Owning The Destination Lifetime")]
    [InlineData(false)] [InlineData(true)]
    public async Task VerifiesArtifactContent(bool chunked)
    {
        var bytes = Enumerable.Range(0, 150001).Select(index => (byte)(index % 251)).ToArray();
        await using var server = new StoreTransferFixture(chunked ? "" : $"Content-Length: {bytes.Length}\r\n", bytes, chunked);
        using var client = Client();
        using var response = await client.GetAsync(server.Address, HttpCompletionOption.ResponseHeadersRead);
        using var output = new MemoryStream();
        await StoreResponseReader.Artifact(response, output, bytes.Length, Sha(bytes), default);
        Assert.Equal(bytes, output.ToArray()); Assert.True(output.CanWrite);
    }

    [Theory(DisplayName = "Invalid Actual Artifact Responses Never Return A Validated Payload")]
    [InlineData("digest")] [InlineData("declared-length")] [InlineData("truncated")]
    [InlineData("chunked-overflow")] [InlineData("encoded")]
    public async Task RejectsInvalidTransfers(string fault)
    {
        var bytes = "immutable archive bytes"u8.ToArray();
        var chunked = fault == "chunked-overflow";
        var length = fault switch
        { "declared-length" or "chunked-overflow" => bytes.Length - 1, "truncated" => bytes.Length + 1, _ => bytes.Length };
        var headers = chunked ? "" : $"Content-Length: {(fault == "truncated" ? bytes.Length + 1 : bytes.Length)}\r\n";
        if (fault == "encoded") headers += "Content-Encoding: gzip\r\n";
        await using var server = new StoreTransferFixture(headers, bytes, chunked);
        using var client = Client();
        using var response = await client.GetAsync(server.Address, HttpCompletionOption.ResponseHeadersRead);
        using var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<IOException>(() => StoreResponseReader.Artifact(response, output, length,
            fault == "digest" ? new string('0', 64) : Sha(bytes), default));
        Assert.True(output.CanWrite);
    }

    [Fact(DisplayName = "Cancelling An Actual Suspended Body Stops The Reader And Leaves The Caller Destination Open")]
    public async Task CancelsSuspendedBodies()
    {
        await using var server = new StoreTransferFixture("Content-Length: 32\r\n", [], suspendBody: true);
        using var client = Client();
        using var response = await client.GetAsync(server.Address, HttpCompletionOption.ResponseHeadersRead);
        await server.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        using var output = new MemoryStream();
        var read = StoreResponseReader.Artifact(response, output, 32, new string('0', 64), cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(output.CanWrite); Assert.Empty(output.ToArray());
    }

    private static HttpClient Client() => ProbeHttpClient.CreateStore(new(ProxyMode.None, null, ""), null);
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
