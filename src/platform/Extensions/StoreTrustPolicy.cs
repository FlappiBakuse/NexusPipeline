using System.Collections.Immutable;
using System.Net;
using NexusPipeline.Platform.Security;
using NexusPipeline.Core.Extensions;

namespace NexusPipeline.Platform.Extensions;

internal sealed class StoreTrustPolicy
{
    private readonly MetadataSignatureKeys _keys;
    private readonly ImmutableArray<Uri> _artifactRoots;
    private readonly Uri _metadataRoot;
    private readonly long _stableFloor, _developFloor;
    internal Version HostVersion { get; }
    internal string SdkVersion { get; }

    internal StoreTrustPolicy(IReadOnlyDictionary<string, byte[]> publicKeys, Uri metadataRoot,
        IReadOnlyList<Uri> artifactRoots, long stableFloor, long developFloor, Version hostVersion, string sdkVersion)
    {
        if (publicKeys.Count is < 1 or > 8 || artifactRoots.Count is < 1 or > 8 ||
            stableFloor is < 1 or > CatalogSequencePolicy.MaximumSequence ||
            developFloor is < 1 or > CatalogSequencePolicy.MaximumSequence ||
            sdkVersion != "0.1.0" || hostVersion.Major < 0) throw Invalid();
        _metadataRoot = RequireRoot(metadataRoot);
        _artifactRoots = artifactRoots.Select(RequireRoot).ToImmutableArray();
        _keys = new(publicKeys, Invalid); _stableFloor = stableFloor; _developFloor = developFloor;
        HostVersion = hostVersion; SdkVersion = sdkVersion;
    }

    internal long Floor(ExtensionStoreChannel channel) => channel switch
    { ExtensionStoreChannel.Stable => _stableFloor, ExtensionStoreChannel.Develop => _developFloor, _ => throw Invalid() };

    internal void Verify(string keyId, ReadOnlySpan<byte> original, ReadOnlySpan<byte> signature)
        => _keys.Verify(keyId, original, signature);

    internal Uri PointerUrl(ExtensionStoreChannel channel) => new(_metadataRoot, channel switch
    { ExtensionStoreChannel.Stable => "stable/index.json", ExtensionStoreChannel.Develop => "develop/index.json", _ => throw Invalid() });

    internal void RequireCatalogUrls(ExtensionStoreChannel channel, Guid generation, string catalog, string signature)
    {
        var prefix = channel switch { ExtensionStoreChannel.Stable => "stable", ExtensionStoreChannel.Develop => "develop", _ => throw Invalid() };
        var path = prefix + "/generations/" + generation.ToString("D") + "/";
        if (catalog != new Uri(_metadataRoot, path + "catalog.json").AbsoluteUri ||
            signature != new Uri(_metadataRoot, path + "catalog.sig.json").AbsoluteUri) throw Invalid();
    }

    internal void RequireArtifactUrl(string value)
    {
        var uri = RequireHttps(value);
        if (!_artifactRoots.Any(root => uri.Scheme == root.Scheme && uri.IdnHost == root.IdnHost &&
            uri.Port == root.Port && uri.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal) &&
            uri.AbsolutePath.Length > root.AbsolutePath.Length)) throw Invalid();
    }

    private static Uri RequireRoot(Uri value)
    {
        var uri = RequireHttps(value.OriginalString);
        if (!uri.AbsolutePath.EndsWith('/') || uri.AbsolutePath == "/") throw Invalid();
        return uri;
    }

    private static Uri RequireHttps(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.IsLoopback || uri.HostNameType != UriHostNameType.Dns || IPAddress.TryParse(uri.Host, out _) ||
            !uri.IdnHost.Contains('.') || uri.IdnHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            value != uri.AbsoluteUri || uri.AbsolutePath.Contains('%') || uri.AbsolutePath.Contains('\\')) throw Invalid();
        return uri;
    }

    private static IOException Invalid() => new("extension_store_trust_unavailable");
}
