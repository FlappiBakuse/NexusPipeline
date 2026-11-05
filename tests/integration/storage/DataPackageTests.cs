using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class DataPackageTests
{
    [Theory(DisplayName = "Current Pure Data Maa Packages Admit Their Declared Bytes And Reject Additional Native Worker Compiler Or Frontend Members")]
    [InlineData("maaend")]
    [InlineData("maas")]
    public void RejectsForeignMembers(string extensionId)
    {
        using var set = JsonDocument.Parse(File.ReadAllBytes(Environment.GetEnvironmentVariable("NEXUS_PACKAGED_SET")!));
        var row = set.RootElement.GetProperty("runs").EnumerateArray().Single(item => item.GetProperty("label").GetString() == extensionId + "-package");
        using var input = new BuiltExtensionInputs(extensionId, row.GetProperty("receipt").GetString()!, explicitPath: true);
        using (var valid = new BoundPackageFixture(input.Archive, input.Manifest, input.ManifestBytes, null, NexusPipeline.Core.Extensions.ExtensionStoreChannel.Stable))
        {
            valid.Publish(); using var owner = valid.Acquire(); using var package = valid.Prepare(owner);
            Assert.Equal(extensionId, package.Manifest.ExtensionId);
        }
        foreach (var name in new[] { "native/core.dll", "upstream.exe", "worker/main.js", "compiler/main.js", "frontend/main.js" })
        {
            var directory = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "data-members-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); var altered = Path.Combine(directory, "altered.zip"); File.Copy(input.Archive, altered);
            using (var zip = ZipFile.Open(altered, ZipArchiveMode.Update))
            using (var stream = zip.CreateEntry(name).Open()) stream.Write("synthetic undeclared payload"u8);
            using var invalid = new BoundPackageFixture(altered, input.Manifest, input.ManifestBytes, null, NexusPipeline.Core.Extensions.ExtensionStoreChannel.Stable);
            invalid.Publish(); using var owner = invalid.Acquire();
            Assert.Throws<IOException>(() => invalid.Prepare(owner));
        }
        input.VerifyUnchanged();
    }
}
