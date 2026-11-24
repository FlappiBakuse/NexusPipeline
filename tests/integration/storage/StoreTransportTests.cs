using System.Collections.Concurrent;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class StoreTransportTests
{
    [Fact(DisplayName = "The Shared Transport Returns Only The Opaque Catalog Verified From All Three Original Responses")]
    public async Task ReturnsVerifiedCatalog()
    {
        using var fixture = new BoundPackageFixture(); var signed = fixture.Signed();
        using var handler = Metadata(signed);
        using var transport = Transport(fixture, handler);
        var verified = await transport.Catalog(ExtensionStoreChannel.Stable, default);
        using var pointer = JsonDocument.Parse(signed.Pointer);
        using var data = JsonDocument.Parse(signed.Catalog);
        Assert.Equal(ExtensionStoreChannel.Stable, verified.Identity.Channel);
        Assert.Equal(pointer.RootElement.GetProperty("generationId").GetGuid(), verified.Identity.GenerationId);
        Assert.Equal(data.RootElement.GetProperty("generationId").GetGuid(), verified.Identity.GenerationId);
        Assert.Equal(pointer.RootElement.GetProperty("sequence").GetInt64(), verified.Identity.Sequence);
        Assert.Equal(data.RootElement.GetProperty("sequence").GetInt64(), verified.Identity.Sequence);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(signed.Catalog)), verified.Identity.Sha256);
        Assert.Equal(signed.Pointer, verified.PointerBytes()); Assert.Equal(signed.Catalog, verified.CatalogBytes());
        Assert.Equal(signed.Signature, verified.SignatureBytes());
        var observed = handler.Requests.ToArray();
        Assert.Equal(PointerUri, observed[0]);
        Assert.Equal(MetadataUris(signed).Keys.Order(StringComparer.Ordinal), observed.Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(0, fixture.Catalog.Count("extension_catalog_acceptances"));
    }

    [Theory(DisplayName = "Pointer Response Policy Rejects Before Any Catalog Or Signature Request")]
    [InlineData("redirect")] [InlineData("catalog-url")] [InlineData("channel")]
    public async Task RejectsPointerPolicies(string fault)
    {
        using var fixture = new BoundPackageFixture(); var signed = fixture.Signed();
        var pointer = fault switch
        {
            "catalog-url" => ExtensionCatalogFixture.Edit(signed.Pointer, "catalogUrl", "https://other.example.test/catalog.json"),
            "channel" => ExtensionCatalogFixture.Edit(signed.Pointer, "channel", "develop"),
            _ => signed.Pointer
        };
        using var handler = new Handler((request, _) => fault == "redirect"
            ? Redirect(request, "https://other.example.test/index.json") : Reply(request, pointer));
        using var transport = Transport(fixture, handler);
        await Assert.ThrowsAsync<IOException>(() => transport.Catalog(ExtensionStoreChannel.Stable, default));
        Assert.Single(handler.Requests); Assert.Equal(0, fixture.Catalog.Count("extension_catalog_acceptances"));
    }

    [Fact(DisplayName = "The Shared Catalog Transport Does Not Grant Authority To Changed Original Signed Bytes")]
    public async Task RejectsChangedSignatures()
    {
        using var fixture = new BoundPackageFixture(); var signed = fixture.Signed();
        var changed = signed with { Catalog = signed.Catalog.Concat(" "u8.ToArray()).ToArray() };
        using var handler = Metadata(changed); using var transport = Transport(fixture, handler);
        var failure = await Assert.ThrowsAsync<IOException>(() => transport.Catalog(ExtensionStoreChannel.Stable, default));
        Assert.Equal("extension_catalog_signature_invalid", failure.Message);
        var allowed = MetadataUris(signed);
        Assert.Equal(PointerUri, handler.Requests.First());
        Assert.Contains(CatalogAddress(signed, "catalog.json"), handler.Requests);
        Assert.All(handler.Requests, uri => Assert.Contains(uri, allowed.Keys));
        Assert.Equal(0, fixture.Catalog.Count("extension_catalog_acceptances"));
    }

    [Theory(DisplayName = "The Shared Artifact Transport Follows Only The Bounded Approved Chain Then Verifies The Original Payload")]
    [InlineData(0)] [InlineData(1)] [InlineData(3)]
    public async Task VerifiesApprovedRedirects(int redirects)
    {
        using var fixture = new BoundPackageFixture(); var bytes = File.ReadAllBytes(fixture.Archive);
        var catalog = fixture.Catalog.Verify(fixture.Signed());
        using var handler = new Handler((request, index) => index < redirects
            ? Redirect(request, new Uri(ExtensionCatalogFixture.Artifacts, "hop-" + index + ".zip").AbsoluteUri)
            : Reply(request, bytes));
        using var transport = Transport(fixture, handler); using var output = new MemoryStream();
        await transport.Artifact(catalog, "fixture-plugin", output, default);
        Assert.Equal(redirects + 1, handler.Requests.Count); Assert.Equal(bytes, output.ToArray());
    }

    [Fact(DisplayName = "An Excess Artifact Redirect Cannot Contact Another Source Or Return Verified Destination Bytes")]
    public async Task BoundsArtifactRedirects()
    {
        using var fixture = new BoundPackageFixture(); var catalog = fixture.Catalog.Verify(fixture.Signed());
        using var handler = new Handler((request, index) => Redirect(request,
            new Uri(ExtensionCatalogFixture.Artifacts, "hop-" + index + ".zip").AbsoluteUri));
        using var transport = Transport(fixture, handler); using var output = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(() => transport.Artifact(catalog, "fixture-plugin", output, default));
        Assert.Equal(4, handler.Requests.Count); Assert.Empty(output.ToArray());
    }

    [Theory(DisplayName = "An Unapproved Artifact Hop Is Rejected Before It Can Be Requested")]
    [InlineData("https://other.example.test/releases/download/value.zip")]
    [InlineData("http://assets.example.test/releases/download/value.zip")]
    [InlineData("https://127.0.0.1/releases/download/value.zip")]
    [InlineData("https://assets.example.test/elsewhere/value.zip")]
    [InlineData("https://assets.example.test/releases/download/value.zip?token=fixture")]
    [InlineData("https://user:password@assets.example.test/releases/download/value.zip")]
    public async Task RejectsUnapprovedHops(string location)
    {
        using var fixture = new BoundPackageFixture(); var catalog = fixture.Catalog.Verify(fixture.Signed());
        using var handler = new Handler((request, _) => Redirect(request, location));
        using var transport = Transport(fixture, handler); using var output = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(() => transport.Artifact(catalog, "fixture-plugin", output, default));
        Assert.Single(handler.Requests); Assert.Empty(output.ToArray());
    }

    [Theory(DisplayName = "A Response Whose Actual Request Was Rewritten Cannot Be Used As Metadata Or Artifact")]
    [InlineData(false)] [InlineData(true)]
    public async Task RejectsRewrittenRequests(bool artifact)
    {
        using var fixture = new BoundPackageFixture(); var signed = fixture.Signed();
        using var handler = new Handler((_, _) => Reply(new(HttpMethod.Get, "https://other.example.test/value"),
            artifact ? File.ReadAllBytes(fixture.Archive) : signed.Pointer));
        using var transport = Transport(fixture, handler); using var output = new MemoryStream();
        if (artifact)
            await Assert.ThrowsAsync<IOException>(() => transport.Artifact(fixture.Catalog.Verify(signed), "fixture-plugin", output, default));
        else await Assert.ThrowsAsync<IOException>(() => transport.Catalog(ExtensionStoreChannel.Stable, default));
        Assert.Single(handler.Requests); Assert.Empty(output.ToArray());
    }

    [Fact(DisplayName = "Each Store Work Freezes Network Settings Across Metadata Responses And Artifact Redirects")]
    public async Task FreezesWorkClient()
    {
        using var fixture = new BoundPackageFixture(); var signed = fixture.Signed();
        var archive = File.ReadAllBytes(fixture.Archive); var epoch = 1; var nextClient = 0;
        var open = new ConcurrentDictionary<int, byte>();
        var observed = new ConcurrentQueue<(string Uri, int Epoch)>();
        var routes = MetadataUris(signed);
        var artifactUri = new Uri(ExtensionCatalogFixture.Artifacts, "v0.5.0/FixturePlugin.zip").AbsoluteUri;
        var redirectUri = new Uri(ExtensionCatalogFixture.Artifacts, "next.zip").AbsoluteUri;
        using var transport = new StoreHttpTransport(fixture.Catalog.Contracts, new StoreTrustPolicy(
            new Dictionary<string, byte[]> { ["fixture-only"] = fixture.Catalog.Signer.ExportSubjectPublicKeyInfo() },
            ExtensionCatalogFixture.MetadataRoot, [ExtensionCatalogFixture.Artifacts], 1, 1, new(0, 18, 0), "0.1.0"), () =>
        {
            var captured = Volatile.Read(ref epoch);
            var clientId = Interlocked.Increment(ref nextClient);
            Assert.True(open.TryAdd(clientId, 0));
            return new HttpClient(new WorkHandler(request =>
            {
                var uri = request.RequestUri!.AbsoluteUri;
                observed.Enqueue((uri, captured));
                if (uri == PointerUri) Volatile.Write(ref epoch, 2);
                if (routes.TryGetValue(uri, out var bytes)) return Reply(request, bytes);
                if (uri == artifactUri) return Redirect(request, redirectUri);
                if (uri == redirectUri) return Reply(request, archive);
                throw new IOException("fixture_unexpected_work_request");
            }, () => Assert.True(open.TryRemove(clientId, out _))));
        });
        var catalog = await transport.Catalog(ExtensionStoreChannel.Stable, default);
        var metadata = observed.ToArray();
        Assert.Equal(routes.Keys.Order(StringComparer.Ordinal), metadata.Select(item => item.Uri).Distinct().Order(StringComparer.Ordinal));
        Assert.All(metadata, item => Assert.Equal(1, item.Epoch));
        Assert.Empty(open);
        using var output = new MemoryStream(); await transport.Artifact(catalog, "fixture-plugin", output, default);
        var downloads = observed.Skip(metadata.Length).ToArray();
        Assert.Equal(new[] { artifactUri, redirectUri }, downloads.Select(item => item.Uri));
        Assert.All(downloads, item => Assert.Equal(2, item.Epoch));
        Assert.Equal(archive, output.ToArray());
        Assert.Empty(open);
    }

    private sealed class WorkHandler(Func<HttpRequestMessage, HttpResponseMessage> reply, Action disposed) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(reply(request)); }
        protected override void Dispose(bool disposing) { try { base.Dispose(disposing); } finally { if (disposing) disposed(); } }
    }

    private static StoreHttpTransport Transport(BoundPackageFixture fixture, HttpMessageHandler handler) =>
        new(fixture.Catalog.Contracts, new StoreTrustPolicy(
            new Dictionary<string, byte[]> { ["fixture-only"] = fixture.Catalog.Signer.ExportSubjectPublicKeyInfo() },
            ExtensionCatalogFixture.MetadataRoot, [ExtensionCatalogFixture.Artifacts], 1, 1, new(0, 18, 0), "0.1.0"),
            () => new HttpClient(handler, disposeHandler: false));

    private static string PointerUri => new Uri(ExtensionCatalogFixture.MetadataRoot, "stable/index.json").AbsoluteUri;

    private static Dictionary<string, byte[]> MetadataUris(SignedCatalogBytes signed) => new(StringComparer.Ordinal)
    {
        [PointerUri] = signed.Pointer,
        [CatalogAddress(signed, "catalog.json")] = signed.Catalog,
        [CatalogAddress(signed, "catalog.sig.json")] = signed.Signature
    };

    private static Handler Metadata(SignedCatalogBytes signed)
    {
        var allowed = MetadataUris(signed);
        return new((request, _) => allowed.TryGetValue(request.RequestUri!.AbsoluteUri, out var body)
            ? Reply(request, body) : throw new IOException("fixture_unexpected_request"));
    }

    private static string CatalogAddress(SignedCatalogBytes signed, string file)
    {
        using var json = System.Text.Json.JsonDocument.Parse(signed.Pointer);
        return new Uri(ExtensionCatalogFixture.MetadataRoot, "stable/generations/" +
            json.RootElement.GetProperty("generationId").GetString() + "/" + file).AbsoluteUri;
    }

    private static HttpResponseMessage Reply(HttpRequestMessage request, byte[] bytes) =>
        new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage Redirect(HttpRequestMessage request, string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = request };
        response.Headers.Location = new Uri(location, UriKind.Absolute); return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> reply) : HttpMessageHandler
    {
        private int _requests;
        internal ConcurrentQueue<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _requests) - 1;
            Requests.Enqueue(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(reply(request, index));
        }
    }
}
