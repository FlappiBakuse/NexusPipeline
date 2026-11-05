using NexusPipeline.Platform.Storage.Sqlite;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Maintenance;
using NexusPipeline.Tests.Fixtures;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class FeedStoreTests
{
    [Fact(DisplayName = "Host Feed Watermarks And Catalog Watermarks Stay Independent Across The Actual Database Reopen")]
    public void SeparatesWatermarkDomains()
    {
        using var owner = new ExtensionCatalogFixture(); using var input = new UpdateFeedFixture();
        var verifier = input.Verifier();
        var store = new UpdateFeedStore(owner.Instance, owner.Database, owner.Lease, owner.Resources, owner.Contracts, verifier);
        var catalog = owner.Verify(owner.Signed(400)); owner.Store.Accept(Guid.NewGuid(), "local", catalog);
        var firstBytes = input.Signed(input.Document(2));
        var first = verifier.Verify(firstBytes.Feed, firstBytes.Signature, HostUpdateChannel.Stable);
        store.Accept(Guid.NewGuid(), "local", first);
        var nextBytes = input.Signed(input.Document(3));
        var next = verifier.Verify(nextBytes.Feed, nextBytes.Signature, HostUpdateChannel.Stable);
        store.Accept(Guid.NewGuid(), "local", next);
        var betaBytes = input.Signed(input.Document(1, "prerelease", "0.18.0-beta.1"));
        var beta = verifier.Verify(betaBytes.Feed, betaBytes.Signature, HostUpdateChannel.Prerelease);
        store.Accept(Guid.NewGuid(), "local", beta);
        Assert.Throws<InvalidOperationException>(() => store.Accept(Guid.NewGuid(), "local", first));
        Assert.Equal(catalog.Identity, owner.Store.ReadCached(ExtensionStoreChannel.Stable)!.Identity);
        var reopened = StateDatabase.OpenExisting(owner.Instance, Path.Combine(owner.Instance.RuntimeRoot, "update-reopen-diagnostic"));
        var cold = new UpdateFeedStore(owner.Instance, reopened, owner.Lease, owner.Resources, owner.Contracts, verifier);
        Assert.Equal(next.Feed.Identity, cold.ReadCached(HostUpdateChannel.Stable)!.Feed.Identity);
        Assert.Equal(beta.Feed.Identity, cold.ReadCached(HostUpdateChannel.Prerelease)!.Feed.Identity);
        var replacementBytes = input.Signed(input.Document(3));
        var replacement = verifier.Verify(replacementBytes.Feed, replacementBytes.Signature, HostUpdateChannel.Stable);
        Assert.Throws<InvalidOperationException>(() => cold.Accept(Guid.NewGuid(), "local", replacement));
        Assert.Equal(next.Feed.Identity, cold.ReadCached(HostUpdateChannel.Stable)!.Feed.Identity);
    }

    [Fact(DisplayName = "Actual Feed Sql Rollback Keeps The Operation And Watermark Atomic And Allows Only The Original Retry")]
    public void RevertsFeedFailure()
    {
        using var owner = new ExtensionCatalogFixture(); using var input = new UpdateFeedFixture();
        var verifier = input.Verifier();
        var store = new UpdateFeedStore(owner.Instance, owner.Database, owner.Lease, owner.Resources, owner.Contracts, verifier);
        var bytes = input.Signed(input.Document());
        var verified = verifier.Verify(bytes.Feed, bytes.Signature, HostUpdateChannel.Stable); var operation = Guid.NewGuid();
        owner.Sql("CREATE TRIGGER stop_feed_head BEFORE INSERT ON trusted_update_feed_heads BEGIN SELECT RAISE(ABORT,'controlled'); END;");
        Assert.Throws<SqliteException>(() => store.Accept(operation, "local", verified));
        owner.Sql("DROP TRIGGER stop_feed_head;");
        Assert.Equal(0, owner.Count("operations")); Assert.Null(store.ReadCached(HostUpdateChannel.Stable));
        store.Accept(operation, "local", verified); store.Accept(operation, "local", verified);
        Assert.Equal(1, owner.Count("operations"));
        Assert.Equal(verified.Feed.Identity, store.ReadCached(HostUpdateChannel.Stable)!.Feed.Identity);
    }
    [Fact(DisplayName = "A Custom Metadata Root Is Actually Requested Without Changing The Signature Or Artifact Trust")]
    public async Task BindsCustomMetadata()
    {
        using var input = new UpdateFeedFixture(); var signed = input.Signed(input.Document());
        var trust = new UpdateTrustPolicy(new Dictionary<string, byte[]> { ["fixture-only"] = input.Signer.ExportSubjectPublicKeyInfo() },
            UpdateFeedFixture.FeedRoot, UpdateFeedFixture.StoreRoot, [UpdateFeedFixture.Artifacts], 1, 1, new(0, 18, 0));
        var handler = new MirrorHandler(signed.Feed, signed.Signature);
        var transport = new UpdateHttpTransport(trust, () => new HttpClient(handler, disposeHandler: false));
        var result = await transport.Feed(HostUpdateChannel.Stable, input.Verifier(), default, "https://mirror.example.test/metadata/update/1/");
        Assert.Equal(1, result.Feed.Identity.Sequence);
        Assert.Equal(new[] { "https://mirror.example.test/metadata/update/1/stable/feed.json", "https://mirror.example.test/metadata/update/1/stable/feed.sig.json" }, handler.Requests);
        Assert.Throws<IOException>(() => trust.RequireArtifact("https://mirror.example.test/arbitrary/Host.zip"));
        await Assert.ThrowsAsync<IOException>(() => transport.Feed(HostUpdateChannel.Stable, input.Verifier(), default, "https://127.0.0.1/metadata/"));
    }

    private sealed class MirrorHandler(byte[] feed, byte[] signature) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { RequestMessage = request,
                Content = new ByteArrayContent(request.RequestUri.AbsolutePath.EndsWith("feed.sig.json", StringComparison.Ordinal) ? signature : feed) });
        }
    }

}
