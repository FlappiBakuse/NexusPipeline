using System.Runtime.Versioning;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class CleanupBarrierTests
{
    [Fact(DisplayName = "An Installed Native Extension Keeps Its Package Locked After Failed Cleanup")]
    public async Task HoldsFailedCleanup()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "native-cleanup-fail");
        var installed = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        Assert.True(installed.IsEnabled);
        Assert.Single(Directory.EnumerateFiles(fixture.ActivationDirectory, "native-loaded-*.json"));
        fixture.Prepared.Dispose();
        fixture.Verified.Dispose();
        fixture.Root.Dispose();

        var failure = await Assert.ThrowsAsync<AggregateException>(() => fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)));
        var causes = failure.Flatten().InnerExceptions;
        Assert.Contains(causes, item => item.Message == "fixture_native_destructor_failed");
        Assert.Single(Directory.EnumerateFiles(fixture.ActivationDirectory, "native-closed-*"));
        Assert.Empty(fixture.Activator.ReadPublishedScopes());

        var manifest = Path.Combine(fixture.Target, "manifest.json");
        var locked = Assert.Throws<IOException>(() =>
        {
            using var opened = File.Open(manifest, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
        Assert.Equal(32, locked.HResult & 0xffff);
        Assert.Contains(causes, item => item.Message == "extension_runtime_cleanup_retained");
    }
}
