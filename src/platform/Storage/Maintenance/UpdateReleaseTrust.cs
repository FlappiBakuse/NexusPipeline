namespace NexusPipeline.Platform.Storage.Maintenance;

public sealed class UpdateReleaseTrust
{
    internal UpdateTrustPolicy Policy { get; }

    public UpdateReleaseTrust(IReadOnlyDictionary<string, byte[]> publicKeys, Uri feedRoot, Uri storeRoot,
        IReadOnlyList<Uri> artifactRoots, long stableFloor, long prereleaseFloor)
        : this(publicKeys, feedRoot, storeRoot, artifactRoots, stableFloor, prereleaseFloor, "production") { }

    internal UpdateReleaseTrust(IReadOnlyDictionary<string, byte[]> publicKeys, Uri feedRoot, Uri storeRoot,
        IReadOnlyList<Uri> artifactRoots, long stableFloor, long prereleaseFloor, string payloadProfile)
    {
        Policy = new(publicKeys, feedRoot, storeRoot, artifactRoots, stableFloor, prereleaseFloor, new(0, 18, 0), payloadProfile);
    }

    internal static UpdateReleaseTrust? Configured()
    {
#if NEXUS_TEST_HOST
        return HostUpdateTrust.Create();
#else
        return Security.ReleaseTrustConfiguration.Current?.Updates;
#endif
    }
}
