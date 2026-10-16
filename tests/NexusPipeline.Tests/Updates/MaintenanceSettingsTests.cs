using NexusPipeline.Modules.Updates;
using NexusPipeline.Platform.Storage;
using Xunit;

namespace NexusPipeline.Tests.Updates;

public sealed class MaintenanceSettingsTests
{
    [Fact]
    public void ReadsHostAtomicSettingsWithoutChangingTheirBytes()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-maintenance-settings-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(root, "config", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            JsonUtil.WriteAtomic(file, "{\"HostLocale\":\"en-US\",\"UpdateSourceUrl\":\"https://fixture.example/releases\",\"UpdateChannel\":\"stable\",\"FutureSetting\":true}");
            byte[] original = File.ReadAllBytes(file);
            Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, original[..3]);
            var settings = MaintenanceSettings.Read(root);
            Assert.Equal("en-US", settings.Locale);
            Assert.Equal("stable", settings.Update.UpdateChannel);
            Assert.Equal(original, File.ReadAllBytes(file));
        }
        finally { Directory.Delete(root, true); }
    }
}
