using System.Net;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Extensions;

internal sealed class StoreHttpTransport : IDisposable
{
    private readonly ContractReader _contracts;
    private readonly StoreTrustPolicy _trust;
    private readonly ExtensionCatalogVerifier _verifier;
    private readonly Func<HttpClient> _createClient;
    private volatile bool _disposed;

    internal StoreHttpTransport(ContractReader contracts, StoreTrustPolicy trust, Func<HttpClient> createClient)
    {
        ArgumentNullException.ThrowIfNull(createClient);
        _contracts = contracts; _trust = trust;
        _verifier = new(contracts, trust); _createClient = createClient;
    }

    private HttpClient CreateClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _createClient();
    }

    internal async Task<VerifiedExtensionCatalog> Catalog(ExtensionStoreChannel channel, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var client = CreateClient();
        var pointer = await Metadata(client, _trust.PointerUrl(channel), timeout.Token);
        var hint = _contracts.Read<StorePointer>(pointer, "store-pointer.schema.json");
        var expected = channel switch
        { ExtensionStoreChannel.Stable => "stable", ExtensionStoreChannel.Develop => "develop", _ => throw Invalid() };
        if (hint.Channel != expected || !Guid.TryParseExact(hint.GenerationId, "D", out var generation) || generation == Guid.Empty)
            throw Invalid();
        _trust.RequireCatalogUrls(channel, generation, hint.CatalogUrl, hint.SignatureUrl);
        var catalog = await Metadata(client, new(hint.CatalogUrl), timeout.Token);
        var signature = await Metadata(client, new(hint.SignatureUrl), timeout.Token);
        return _verifier.Verify(pointer, catalog, signature, channel);
    }

    internal async Task Artifact(VerifiedExtensionCatalog catalog, string extensionId, Stream destination,
        CancellationToken cancellationToken)
    {
        var entry = catalog.Entries.SingleOrDefault(item => item.ExtensionId == extensionId) ?? throw Invalid();
        _trust.RequireArtifactUrl(entry.ArtifactUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var client = CreateClient();
        var current = new Uri(entry.ArtifactUrl);
        for (var hop = 0; ; hop++)
        {
            _trust.RequireArtifactUrl(current.AbsoluteUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            RequireFinalRequest(response, current);
            if (IsRedirect(response.StatusCode))
            {
                if (hop == 3 || response.Headers.Location is not { } location) throw Invalid();
                current = location.IsAbsoluteUri ? location : new(current, location);
                _trust.RequireArtifactUrl(current.AbsoluteUri);
                continue;
            }
            await StoreResponseReader.Artifact(response, destination, entry.ArtifactBytes, entry.ArtifactSha256, timeout.Token);
            return;
        }
    }

    private static async Task<byte[]> Metadata(HttpClient client, Uri address, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        RequireFinalRequest(response, address);
        return await StoreResponseReader.Metadata(response, cancellationToken);
    }

    private static void RequireFinalRequest(HttpResponseMessage response, Uri requested)
    {
        if (response.RequestMessage?.RequestUri?.AbsoluteUri != requested.AbsoluteUri) throw Invalid();
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
        HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    private static IOException Invalid() => new("extension_store_download_invalid");
    public void Dispose() => _disposed = true;
}
