namespace NexusPipeline.Core.Extensions;

public sealed record ExtensionCatalogEntry(string ExtensionId, string ArtifactName, string PackageVersion,
    string Kind, string MinHostVersion, string SdkVersion, string ArtifactUrl, long ArtifactBytes,
    string ArtifactSha256, string ManifestSha256)
{
    public ExtensionMetadata? Metadata { get; init; }
}
