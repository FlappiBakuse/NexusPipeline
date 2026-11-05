using System.Net;
using System.Runtime.Versioning;
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
        Assert.Equal(fixture.Catalog.Verify(signed).Identity, verified.Identity);
        Assert.Equal(signed.Pointer, verified.PointerBytes()); Assert.Equal(signed.Catalog, verified.CatalogBytes());
        Assert.Equal(signed.Signature, verified.SignatureBytes());
        Assert.Equal(new[] { new Uri(ExtensionCatalogFixture.MetadataRoot, "stable/index.json").AbsoluteUri,
            CatalogAddress(signed, "catalog.json"), CatalogAddress(signed, "catalog.sig.json") }, handler.Requests);
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
        await Assert.ThrowsAsync<IOException>(() => transport.Catalog(ExtensionStoreChannel.Stable, default));
        Assert.Equal(3, handler.Requests.Count); Assert.Equal(0, fixture.Catalog.Count("extension_catalog_acceptances"));
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

    [Fact(DisplayName = "Each New Store Work Freezes One Client Across All Metadata Responses And Artifact Redirects")]
    public async Task FreezesWorkClient()
    {
        using var fixture = new BoundPackageFixture(); var signed = fixture.Signed();
        var archive = File.ReadAllBytes(fixture.Archive); var epoch = 1; var created = 0; var disposed = 0;
        var observed = new List<int>();
        using var transport = new StoreHttpTransport(fixture.Catalog.Contracts, new StoreTrustPolicy(
            new Dictionary<string, byte[]> { ["fixture-only"] = fixture.Catalog.Signer.ExportSubjectPublicKeyInfo() },
            ExtensionCatalogFixture.MetadataRoot, [ExtensionCatalogFixture.Artifacts], 1, 1, new(0, 18, 0), "0.1.0"), () =>
        {
            var captured = epoch; created++; var requests = 0;
            return new HttpClient(new WorkHandler(request =>
            {
                observed.Add(captured); requests++;
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("index.json", StringComparison.Ordinal)) { epoch = 2; return Reply(request, signed.Pointer); }
                if (path.EndsWith("catalog.json", StringComparison.Ordinal)) return Reply(request, signed.Catalog);
                if (path.EndsWith("catalog.sig.json", StringComparison.Ordinal)) return Reply(request, signed.Signature);
                return requests == 1 ? Redirect(request, new Uri(ExtensionCatalogFixture.Artifacts, "next.zip").AbsoluteUri) : Reply(request, archive);
            }, () => disposed++));
        });
        var catalog = await transport.Catalog(ExtensionStoreChannel.Stable, default);
        Assert.Equal(new[] { 1, 1, 1 }, observed); Assert.Equal(1, created); Assert.Equal(1, disposed);
        using var output = new MemoryStream(); await transport.Artifact(catalog, "fixture-plugin", output, default);
        Assert.Equal(new[] { 1, 1, 1, 2, 2 }, observed); Assert.Equal(archive, output.ToArray());
        Assert.Equal(2, created); Assert.Equal(2, disposed);
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

    private static Handler Metadata(SignedCatalogBytes signed) => new((request, index) =>
        Reply(request, index switch { 0 => signed.Pointer, 1 => signed.Catalog, 2 => signed.Signature,
            _ => throw new IOException("fixture_unexpected_request") }));

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
        internal List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); var index = Requests.Count;
            Requests.Add(request.RequestUri!.AbsoluteUri); return Task.FromResult(reply(request, index));
        }
    }
}
