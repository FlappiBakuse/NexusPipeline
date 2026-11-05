namespace NexusPipeline.Platform.Extensions;

internal static class ExtensionTestTrust
{
    internal static ExtensionReleaseTrust Create() => new(
        new Dictionary<string, byte[]>
        {
            ["fixture-only"] = Convert.FromBase64String("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE6uyjBJaylz5W+08fYBHMupD7vnjEOsNOFQGlLksjWMd+NAK85+0rrHkg8zoubm64PNjUvrMV7JOhQKRVUdWDBg==")
        }, new("https://store.example.test/metadata/store/1/"), [new("https://assets.example.test/releases/download/")], 1, 1);
}
