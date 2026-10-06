using System.Collections.Specialized;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.ControlPlane.Http.Static;
using Xunit;
using WebRequest = NexusPipeline.ControlPlane.Http.WebRequest;
using WebResponse = NexusPipeline.ControlPlane.Http.WebResponse;

namespace NexusPipeline.Tests.ControlPlane;

public sealed class EmbeddedFrontendTests
{
    private static readonly byte[] Index = [0xef, 0xbb, 0xbf, 60, 104, 116, 109, 108, 62, 13, 10];
    private static readonly byte[] Script = Encoding.UTF8.GetBytes("export const current = true;");

    private static (EmbeddedFrontendAssetProvider Provider, FrontendAsset Index, FrontendAsset Script) Fixture()
    {
        FrontendAsset Asset(string path, byte[] bytes, string mime, bool immutable)
        {
            string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return new(path, "NexusPipeline.Frontend.Asset." + sha, bytes.Length, sha, mime, immutable);
        }
        FrontendAsset index = Asset("index.html", Index, "text/html; charset=utf-8", false);
        FrontendAsset script = Asset("assets/index-abcdefgh.js", Script, "application/javascript; charset=utf-8", true);
        return (new(index.Sha256, [index, script], name => new MemoryStream(name == index.ResourceName ? Index : Script)), index, script);
    }

    [Fact]
    public void EmbeddedBytesKeepTheirRawIdentityWithoutDiskFiles()
    {
        var fixture = Fixture();
        Assert.Equal(fixture.Index.Sha256, fixture.Provider.FrontendHash);
        Assert.Equal(Index, fixture.Provider.Read(fixture.Provider.Resolve("/")!));
        Assert.Equal(Script, fixture.Provider.Read(fixture.Provider.Resolve("/assets/index-abcdefgh.js")!));
    }

    [Fact]
    public void TraversalReservedPathsAndMissingChunksCannotUseSpaFallback()
    {
        var fixture = Fixture();
        foreach (string path in new[] { "/../index.html", "/%2e%2e/index.html", "/%252e%252e/index.html", "/foo\\bar",
                     "/config/settings.json", "/resources/desktop", "/plugins/secret", "/.nxp/state", "/api/missing",
                     "/assets/missing.js", "/foo//bar", "/C:/file", "/foo/..", "/foo?x" })
            Assert.Null(fixture.Provider.Resolve(path));
        Assert.Equal(fixture.Index, fixture.Provider.Resolve("/settings"));
    }

    [Fact]
    public async Task GetHeadEtagsAndCacheHeadersMatchTheIndexedBytes()
    {
        var fixture = Fixture();
        var get = Context("GET");
        await HttpHelper.ServeFrontendAsync(get.Context, fixture.Provider, "/assets/index-abcdefgh.js");
        Assert.Equal(Script, get.Bytes.ToArray());
        Assert.Equal(Script.Length, get.Context.Response.ContentLength64);
        Assert.Contains("immutable", get.Context.Response.Headers["Cache-Control"]!);
        Assert.Equal(fixture.Script.ContentType, get.Context.Response.ContentType);
        var head = Context("HEAD");
        await HttpHelper.ServeFrontendAsync(head.Context, fixture.Provider, "/");
        Assert.Empty(head.Bytes.ToArray());
        Assert.Equal(Index.Length, head.Context.Response.ContentLength64);
        Assert.Equal("no-cache", head.Context.Response.Headers["Cache-Control"]);
        var cached = Context("GET", '"' + fixture.Index.Sha256 + '"');
        await HttpHelper.ServeFrontendAsync(cached.Context, fixture.Provider, "/index.html");
        Assert.Equal(304, cached.Context.Response.StatusCode);
        Assert.Empty(cached.Bytes.ToArray());
    }

    [Fact]
    public async Task StaticWritesAreRejectedBeforeReadingAnyResource()
    {
        var request = Context("POST");
        int reads = 0;
        var fixture = Fixture();
        var provider = new EmbeddedFrontendAssetProvider(fixture.Index.Sha256, [fixture.Index], _ => { reads++; return new MemoryStream(Index); });
        await HttpHelper.ServeFrontendAsync(request.Context, provider, "/");
        Assert.Equal(405, request.Context.Response.StatusCode);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void CorruptResourcesAndConflictingIndexEntriesFailClosed()
    {
        var fixture = Fixture();
        var corrupt = new EmbeddedFrontendAssetProvider(fixture.Index.Sha256, [fixture.Index], _ => new MemoryStream(new byte[Index.Length]));
        Assert.Throws<InvalidDataException>(() => corrupt.Read(fixture.Index));
        Assert.Throws<InvalidDataException>(() => new EmbeddedFrontendAssetProvider(fixture.Index.Sha256, [fixture.Index, fixture.Index], _ => null));
        Assert.Throws<InvalidDataException>(() => new EmbeddedFrontendAssetProvider(new string('0', 64), [fixture.Index], _ => null));
    }

    private static (WebContext Context, MemoryStream Bytes) Context(string method, string? etag = null)
    {
        var bytes = new MemoryStream();
        var headers = new NameValueCollection();
        if (etag is not null) headers["If-None-Match"] = etag;
        var request = new WebRequest(new Uri("http://127.0.0.1:58731/"), method, new(), headers, Stream.Null, 0, null,
            Encoding.UTF8, false, new IPEndPoint(IPAddress.Loopback, 1000));
        return (new WebContext(request, new WebResponse(bytes)), bytes);
    }
}
