namespace NexusPipeline.ControlPlane.Http.Static;

internal sealed record FrontendAsset(string Path, string ResourceName, long SizeBytes, string Sha256, string ContentType, bool Immutable);

internal interface IFrontendAssetProvider
{
    string FrontendHash { get; }
    FrontendAsset? Resolve(string requestPath);
    byte[] Read(FrontendAsset asset);
}
