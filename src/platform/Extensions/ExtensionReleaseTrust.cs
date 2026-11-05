namespace NexusPipeline.Platform.Extensions;

public sealed class ExtensionReleaseTrust
{
    internal StoreTrustPolicy Policy { get; }

    internal static ExtensionReleaseTrust? Configured()
    {
#if NEXUS_TEST_HOST
        return ExtensionTestTrust.Create();
#else
        return Security.ReleaseTrustConfiguration.Current?.Extensions;
#endif
    }

    public ExtensionReleaseTrust(IReadOnlyDictionary<string, byte[]> publicKeys, Uri metadataRoot,
        IReadOnlyList<Uri> artifactRoots, long stableFloor, long developFloor)
    {
        Policy = new(publicKeys, metadataRoot, artifactRoots, stableFloor, developFloor, new(0, 18, 0), "0.1.0");
    }
}
