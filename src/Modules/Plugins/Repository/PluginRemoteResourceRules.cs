using NexusPipeline.Platform.Networking;

namespace NexusPipeline.Modules.Plugins.Repository;

/// <summary>Official plugin catalog/package/readme resource rules.</summary>
internal static class PluginRemoteResourceRules
{
    private static readonly string[] OfficialInitialHosts =
    {
        "api.github.com",
        "github.com",
        "raw.githubusercontent.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com",
    };

    internal static RemoteResourceRules ForCatalog(Uri source)
        => Create(source, TimeSpan.FromSeconds(30));

    internal static RemoteResourceRules ForPackage(Uri source)
        => Create(source, TimeSpan.FromMinutes(10));

    internal static RemoteResourceRules ForReadme(Uri source)
        => Create(source, TimeSpan.FromSeconds(30));

    private static RemoteResourceRules Create(Uri source, TimeSpan timeout)
    {
        bool official = source.Scheme == Uri.UriSchemeHttps
            && OfficialInitialHosts.Contains(source.Host, StringComparer.OrdinalIgnoreCase);
        string[] redirectHosts =
        {
            "release-assets.githubusercontent.com",
            "objects.githubusercontent.com",
            "github-releases.githubusercontent.com",
        };
        return new RemoteResourceRules(
            source,
            new[] { Uri.UriSchemeHttps },
            official ? redirectHosts : new[] { source.Host },
            sameOriginOnly: !official,
            allowLoopbackHttp: source.IsLoopback,
            timeout,
            maxRedirects: 5,
            rejectUserInfo: true,
            rejectQuery: false,
            rejectFragment: false,
            requireHttpsDefaultPort: official,
            initialAllowedHosts: official ? OfficialInitialHosts : null);
    }
}
