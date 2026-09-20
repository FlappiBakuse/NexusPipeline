using NexusPipeline.Platform.Networking;

namespace NexusPipeline.Modules.Updates;

/// <summary>Host update resource trust rules; product URLs stay out of Platform.</summary>
internal static class UpdateRemoteResourceRules
{
    internal const string DefaultSourceUrl = "https://api.github.com/repos/FlappiBakuse/NexusPipeline/releases";
    internal const string DefaultPolicyHost = "raw.githubusercontent.com";
    internal const string DefaultPolicyPath = "/FlappiBakuse/NexusPipeline/main/update-policy.json";

    private static readonly string[] DefaultAssetHosts =
    {
        "api.github.com",
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com",
        "raw.githubusercontent.com",
    };

    internal static RemoteResourceRules Create(
        Uri sourceUri,
        bool isDefaultSource,
        UpdateResourceKind resourceKind)
    {
        if (!isDefaultSource)
        {
            return new RemoteResourceRules(
                sourceUri,
                new[] { Uri.UriSchemeHttps },
                new[] { sourceUri.Host },
                sameOriginOnly: true,
                allowLoopbackHttp: sourceUri.IsLoopback,
                timeout: TimeSpan.FromMinutes(10),
                rejectUserInfo: true,
                rejectQuery: false,
                rejectFragment: false,
                requireInitialSourceUri: resourceKind == UpdateResourceKind.Manifest);
        }

        IEnumerable<string> hosts = resourceKind switch
        {
            UpdateResourceKind.Manifest => new[] { "api.github.com" },
            UpdateResourceKind.Policy => new[] { DefaultPolicyHost },
            UpdateResourceKind.ReleaseAsset => DefaultAssetHosts,
            _ => throw new ArgumentOutOfRangeException(nameof(resourceKind), resourceKind, null),
        };
        return new RemoteResourceRules(
            sourceUri,
            new[] { Uri.UriSchemeHttps },
            hosts,
            sameOriginOnly: false,
            allowLoopbackHttp: false,
            timeout: TimeSpan.FromMinutes(10),
            rejectUserInfo: true,
            rejectQuery: false,
            rejectFragment: false,
            requireHttpsDefaultPort: true,
            initialAllowedHosts: hosts,
            requireInitialSourceUri: resourceKind == UpdateResourceKind.Manifest);
    }

    internal static bool IsDefaultHost(string host)
        => DefaultAssetHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
}
