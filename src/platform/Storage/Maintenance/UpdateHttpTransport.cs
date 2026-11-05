using System.Net;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Extensions;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed class UpdateHttpTransport(UpdateTrustPolicy trust, Func<HttpClient> createClient)
{
    internal async Task<VerifiedUpdateFeed> Feed(HostUpdateChannel channel, UpdateFeedVerifier verifier,
        CancellationToken cancellation, string? sourceRoot = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var client = createClient();
        var feed = await Metadata(client, trust.FeedUrl(channel, sourceRoot), timeout.Token);
        var signature = await Metadata(client, trust.SignatureUrl(channel, sourceRoot), timeout.Token);
        return verifier.Verify(feed, signature, channel);
    }

    internal async Task Artifact(HostUpdateArtifact artifact, Stream destination, CancellationToken cancellation)
    {
        trust.RequireArtifact(artifact.Url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var client = createClient();
        var current = new Uri(artifact.Url);
        for (var hop = 0; ; hop++)
        {
            trust.RequireArtifact(current.AbsoluteUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            RequireFinalRequest(response, current);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (hop == 3 || response.Headers.Location is not { } next) throw Invalid();
                current = next.IsAbsoluteUri ? next : new(current, next);
                continue;
            }
            await StoreResponseReader.Artifact(response, destination, artifact.SizeBytes, artifact.Sha256,
                timeout.Token, maximumBytes: 1073741824);
            return;
        }
    }

    private static async Task<byte[]> Metadata(HttpClient client, Uri address, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        RequireFinalRequest(response, address);
        return await StoreResponseReader.Metadata(response, cancellation);
    }

    private static void RequireFinalRequest(HttpResponseMessage response, Uri expected)
    {
        if (response.RequestMessage?.RequestUri?.AbsoluteUri != expected.AbsoluteUri) throw Invalid();
    }
    private static IOException Invalid() => new("maintenance_download_invalid");
}
