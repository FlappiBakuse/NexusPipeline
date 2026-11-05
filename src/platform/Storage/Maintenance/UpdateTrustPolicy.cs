using System.Collections.Immutable;
using System.Net;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Security;

namespace NexusPipeline.Platform.Storage.Maintenance;

internal sealed class UpdateTrustPolicy
{
    private readonly MetadataSignatureKeys _keys;
    private readonly Uri _feedRoot, _storeRoot;
    private readonly ImmutableArray<Uri> _artifactRoots;
    private readonly long _stableFloor, _prereleaseFloor;
    internal Version ReaderVersion { get; }
    internal string PayloadProfile { get; }

    internal UpdateTrustPolicy(IReadOnlyDictionary<string, byte[]> publicKeys, Uri feedRoot, Uri storeRoot,
        IReadOnlyList<Uri> artifactRoots, long stableFloor, long prereleaseFloor, Version readerVersion, string payloadProfile = "production")
    {
        if (artifactRoots.Count is < 1 or > 8 || stableFloor is < 1 or > UpdateFeedPolicy.MaximumSequence ||
            prereleaseFloor is < 1 or > UpdateFeedPolicy.MaximumSequence || readerVersion.Major < 0 ||
            payloadProfile is not ("production" or "test")) throw Invalid();
        _keys = new(publicKeys, Invalid); _feedRoot = Root(feedRoot); _storeRoot = Root(storeRoot);
        _artifactRoots = artifactRoots.Select(Root).ToImmutableArray();
        _stableFloor = stableFloor; _prereleaseFloor = prereleaseFloor; ReaderVersion = readerVersion;
        PayloadProfile = payloadProfile;
    }

    internal long Floor(HostUpdateChannel channel) => channel switch
    { HostUpdateChannel.Stable => _stableFloor, HostUpdateChannel.Prerelease => _prereleaseFloor, _ => throw Invalid() };

    internal void Verify(string keyId, ReadOnlySpan<byte> original, ReadOnlySpan<byte> signature) => _keys.Verify(keyId, original, signature);

    internal Uri FeedUrl(HostUpdateChannel channel, string? sourceRoot = null) => new(sourceRoot is null ? _feedRoot : Root(new Uri(sourceRoot)), Channel(channel) + "/feed.json");
    internal Uri SignatureUrl(HostUpdateChannel channel, string? sourceRoot = null) => new(sourceRoot is null ? _feedRoot : Root(new Uri(sourceRoot)), Channel(channel) + "/feed.sig.json");

    internal void RequireStore(UpdateStoreBinding binding)
    {
        var channel = binding.Channel switch
        { ExtensionStoreChannel.Stable => "stable", ExtensionStoreChannel.Develop => "develop", _ => throw Invalid() };
        if (binding.GenerationId == Guid.Empty || binding.Sequence is < 1 or > UpdateFeedPolicy.MaximumSequence) throw Invalid();
        var relative = channel + "/generations/" + binding.GenerationId.ToString("D") + "/";
        if (binding.CatalogUrl != new Uri(_storeRoot, relative + "catalog.json").AbsoluteUri ||
            binding.SignatureUrl != new Uri(_storeRoot, relative + "catalog.sig.json").AbsoluteUri) throw Invalid();
    }

    internal void RequireArtifact(string value)
    {
        var uri = Https(value);
        if (!_artifactRoots.Any(root => uri.Scheme == root.Scheme && uri.IdnHost == root.IdnHost && uri.Port == root.Port &&
            uri.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal) && uri.AbsolutePath.Length > root.AbsolutePath.Length))
            throw Invalid();
    }

    private static string Channel(HostUpdateChannel value) => value switch
    { HostUpdateChannel.Stable => "stable", HostUpdateChannel.Prerelease => "prerelease", _ => throw Invalid() };

    private static Uri Root(Uri value)
    {
        var uri = Https(value.OriginalString);
        if (!uri.AbsolutePath.EndsWith('/') || uri.AbsolutePath == "/") throw Invalid();
        return uri;
    }

    private static Uri Https(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.IsLoopback ||
            uri.HostNameType != UriHostNameType.Dns || IPAddress.TryParse(uri.Host, out _) || !uri.IdnHost.Contains('.') ||
            uri.IdnHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || value != uri.AbsoluteUri ||
            uri.AbsolutePath.Contains('%') || uri.AbsolutePath.Contains('\\')) throw Invalid();
        return uri;
    }

    private static IOException Invalid() => new("update_trust_unavailable");
}
