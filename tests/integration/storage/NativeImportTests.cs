using System.Runtime.Versioning;
using NexusPipeline.Platform.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class NativeImportTests
{
    [Fact(DisplayName = "Native Dependency Discovery Preserves Imports Without Reading An Unrelated Locked PE Overlay")]
    public void SkipsUnrelatedOverlay()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "native-imports-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "image.dll");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "e_sqlite3.dll"), path);
        var expected = InstalledNativeImports.Read(path); Assert.NotEmpty(expected);
        var length = new FileInfo(path).Length;
        using (var extended = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)) extended.SetLength(length + 8388608);
        using var unrelated = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        unrelated.Lock(length, 8388608);
        try { Assert.Equal(expected.ToArray(), InstalledNativeImports.Read(path).ToArray()); }
        finally { unrelated.Unlock(length, 8388608); }
    }

    [Fact(DisplayName = "Unknown Native Image Bytes Are Preserved When Dependency Discovery Refuses Them")]
    public void PreservesUnknownImage()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "native-unknown-" + Guid.NewGuid().ToString("N"));
        byte[] bytes = [1, 2, 3, 4, 5]; File.WriteAllBytes(path, bytes);
        Assert.Throws<BadImageFormatException>(() => InstalledNativeImports.Read(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
