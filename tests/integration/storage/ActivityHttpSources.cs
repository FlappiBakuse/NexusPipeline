using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace NexusPipeline.StorageTests;

internal sealed class ActivityHttpSources : HttpMessageHandler
{
    private readonly List<(FileStream Stream, string Hash)> _pins = [];
    private readonly Dictionary<string, byte[]> _sources = new(StringComparer.Ordinal);
    private readonly HttpMessageInvoker? _network;
    private readonly string _output;
    internal ConcurrentDictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);

    internal ActivityHttpSources(bool live)
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_ACTIVITY_SOURCE_ROOT") ??
            throw new IOException("NEXUS_ACTIVITY_SOURCE_ROOT_required"));
        var owned = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_PACKAGED_EXTENSION_ROOT")!);
        if (!root.StartsWith(owned.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("activity_source_root_unowned");
        for (var current = root; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("activity_source_root_has_link");
        using var receipt = JsonDocument.Parse(Read(Path.Combine(root, "receipts.json")));
        foreach (var item in receipt.RootElement.EnumerateArray())
        {
            Assert.Equal("PASS", item.GetProperty("status").GetString());
            var feed = item.GetProperty("feed").GetString()!;
            if (feed.Length is < 1 or > 32 || feed.Any(value => !char.IsAsciiLetterOrDigit(value) && value != '-'))
                throw new IOException("activity_source_name_invalid");
            var path = Path.Combine(root, feed + ".json");
            var bytes = Read(path);
            Assert.Equal(item.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
            _sources.Add(item.GetProperty("url").GetString()!, bytes);
        }
        Assert.Equal(11, _sources.Count);
        _output = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "activity-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_output);
        if (live) _network = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None });
    }

    internal void VerifyUnchanged()
    {
        foreach (var (stream, expected) in _pins)
        {
            stream.Position = 0;
            Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(stream)));
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.True(_sources.ContainsKey(url), url);
        Assert.Equal("identity", Assert.Single(request.Headers.AcceptEncoding).Value);
        Calls.AddOrUpdate(url, 1, (_, before) => before + 1);
        await Task.Delay(150, cancellationToken);
        var response = _network is null ? new HttpResponseMessage(HttpStatusCode.OK)
        { RequestMessage = request, Content = new ByteArrayContent(_sources[url]) } : await _network.SendAsync(request, cancellationToken);
        if (_network is null) response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        await response.Content.LoadIntoBufferAsync(2097152, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var name = Path.GetFileNameWithoutExtension(request.RequestUri.AbsolutePath);
        var record = Path.Combine(_output, name + "-" + hash);
        if (!File.Exists(record + ".json")) File.WriteAllBytes(record + ".json", bytes);
        File.WriteAllBytes(record + ".receipt.json", JsonSerializer.SerializeToUtf8Bytes(new
        { url, status = (int)response.StatusCode, source = _network is null ? "recorded-source-fixture" : "actual-https",
            hash, sizeBytes = bytes.Length, headers = response.Headers.Concat(response.Content.Headers).ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()) }));
        return response;
    }

    private byte[] Read(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("activity_source_has_link");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _pins.Add((stream, Convert.ToHexStringLower(SHA256.HashData(stream))));
        if (stream.Length is < 1 or > 2097152) throw new IOException("activity_source_size_invalid");
        stream.Position = 0;
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        return bytes;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _network?.Dispose();
            foreach (var (stream, _) in _pins) stream.Dispose();
        }
        base.Dispose(disposing);
    }
}
