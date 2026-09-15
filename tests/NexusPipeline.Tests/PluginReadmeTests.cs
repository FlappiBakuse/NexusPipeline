using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NexusPipeline.Plugins;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class OfficialPluginSourcePathTests
{
    [Theory]
    [InlineData("managed-code", "CustomWallpaper", "plugins/general/CustomWallpaper/README.md")]
    [InlineData("data-specialized", "BetterGI", "plugins/specialized/BetterGI/README.md")]
    public void ReadmeUri_UsesKindSpecificOfficialSourceDirectory(
        string kind,
        string artifactName,
        string expectedPath)
    {
        Assert.True(OfficialPluginSourcePaths.TryGetReadmeUri(
            kind,
            artifactName,
            out Uri? uri,
            out string? error), error);
        Assert.Equal(
            "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/" + expectedPath,
            uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("unknown", "CustomWallpaper")]
    [InlineData("managed-code", "../CustomWallpaper")]
    [InlineData("data-specialized", "CustomWallpaper/")]
    public void ReadmeUri_RejectsUnknownKindOrUnsafeArtifact(
        string kind,
        string artifactName)
    {
        Assert.False(OfficialPluginSourcePaths.TryGetReadmeUri(
            kind,
            artifactName,
            out Uri? uri,
            out string? error));
        Assert.Null(uri);
        Assert.NotNull(error);
    }
}

public sealed class PluginReadmeServiceTests
{
    [Fact]
    public async Task OfficialReadme_UsesSourcePathAndReusesCachedContentAfter304()
    {
        var handler = new QueueHandler(
            CreateResponse(HttpStatusCode.OK, "# cached", etag: "\"v1\""),
            CreateResponse(HttpStatusCode.NotModified, Array.Empty<byte>()));
        var service = new PluginReadmeService(
            _ => new HttpClient(handler, disposeHandler: false),
            cacheTtl: TimeSpan.Zero);
        PluginStoreItem item = CreateStoreItem("data-specialized", "BetterGI");

        PluginReadmeResult first = await service.LoadOfficialAsync(item, CancellationToken.None);
        PluginReadmeResult second = await service.LoadOfficialAsync(item, CancellationToken.None);

        Assert.Null(first.Error);
        Assert.Equal("# cached", first.Markdown);
        Assert.Null(second.Error);
        Assert.Equal("# cached", second.Markdown);
        Assert.Equal(
            "https://raw.githubusercontent.com/FlappiBakuse/NexusPipeline-Plugins/main/plugins/specialized/BetterGI/README.md",
            handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Equal("\"v1\"", handler.Requests[1].Headers.GetValues("If-None-Match").Single());
    }

    [Fact]
    public async Task OfficialReadme_ReturnsCachedMarkdownWhenRefreshFails()
    {
        var handler = new QueueHandler(
            CreateResponse(HttpStatusCode.OK, "# cached"),
            CreateResponse(HttpStatusCode.NotFound, Array.Empty<byte>()));
        var service = new PluginReadmeService(
            _ => new HttpClient(handler, disposeHandler: false),
            cacheTtl: TimeSpan.Zero);
        PluginStoreItem item = CreateStoreItem("managed-code", "CustomWallpaper");

        await service.LoadOfficialAsync(item, CancellationToken.None);
        PluginReadmeResult stale = await service.LoadOfficialAsync(item, CancellationToken.None);

        Assert.Equal("# cached", stale.Markdown);
        Assert.Contains("显示上次缓存", stale.Error);
    }

    [Fact]
    public async Task OfficialReadme_ReturnsErrorWhenNotFoundWithoutCache()
    {
        var handler = new QueueHandler(
            CreateResponse(HttpStatusCode.NotFound, Array.Empty<byte>()));
        var service = new PluginReadmeService(
            _ => new HttpClient(handler, disposeHandler: false));

        PluginReadmeResult result = await service.LoadOfficialAsync(
            CreateStoreItem("managed-code", "CustomWallpaper"),
            CancellationToken.None);

        Assert.True(result.HasReadme);
        Assert.Empty(result.Markdown);
        Assert.Contains("HTTP 404", result.Error);
    }

    [Fact]
    public async Task OfficialReadme_RejectsInvalidUtf8()
    {
        var handler = new QueueHandler(CreateResponse(
            HttpStatusCode.OK,
            new byte[] { 0xff, 0xfe }));
        var service = new PluginReadmeService(
            _ => new HttpClient(handler, disposeHandler: false));

        PluginReadmeResult result = await service.LoadOfficialAsync(
            CreateStoreItem("managed-code", "CustomWallpaper"),
            CancellationToken.None);

        Assert.True(result.HasReadme);
        Assert.Empty(result.Markdown);
        Assert.Contains("读取 README.md 失败", result.Error);
    }

    [Fact]
    public async Task OfficialReadme_RejectsContentAboveSizeLimit()
    {
        var handler = new QueueHandler(CreateResponse(
            HttpStatusCode.OK,
            new byte[(256 * 1024) + 1]));
        var service = new PluginReadmeService(
            _ => new HttpClient(handler, disposeHandler: false));

        PluginReadmeResult result = await service.LoadOfficialAsync(
            CreateStoreItem("managed-code", "CustomWallpaper"),
            CancellationToken.None);

        Assert.True(result.HasReadme);
        Assert.Empty(result.Markdown);
        Assert.Contains("超过 256 KiB", result.Error);
    }

    [Fact]
    public async Task OfficialReadme_DoesNotRequestWhenCatalogDeclaresNoReadme()
    {
        var handler = new QueueHandler();
        var service = new PluginReadmeService(
            _ => new HttpClient(handler, disposeHandler: false));
        PluginStoreItem item = CreateStoreItem("managed-code", "CustomWallpaper") with { HasReadme = false };

        PluginReadmeResult result = await service.LoadOfficialAsync(item, CancellationToken.None);

        Assert.False(result.HasReadme);
        Assert.Empty(handler.Requests);
    }

    private static PluginStoreItem CreateStoreItem(string kind, string artifactName)
    {
        return new PluginStoreItem(
            artifactName.ToLowerInvariant(),
            artifactName,
            artifactName,
            "",
            "",
            "0.1.0",
            kind,
            kind == "managed-code" ? "1.0" : "",
            Array.Empty<string>(),
            "0.1.0",
            false,
            "",
            false,
            true,
            "",
            false,
            "",
            "",
            "available",
            "",
            Array.Empty<PluginChangelogEntry>())
        {
            HasReadme = true,
        };
    }

    private static HttpResponseMessage CreateResponse(
        HttpStatusCode status,
        string content,
        string? etag = null) =>
        CreateResponse(status, Encoding.UTF8.GetBytes(content), etag);

    private static HttpResponseMessage CreateResponse(
        HttpStatusCode status,
        byte[] content,
        string? etag = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(content),
        };
        if (etag is not null)
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        }
        return response;
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        internal QueueHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        internal List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            HttpResponseMessage response = _responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
