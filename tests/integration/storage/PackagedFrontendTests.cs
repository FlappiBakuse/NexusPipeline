using System.Runtime.Versioning;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class PackagedFrontendTests
{
    [Fact(DisplayName = "The Original Built Screenshot Zip Installs Its Actual Assembly And Declared Browser Assets")]
    public async Task InstallsCompletePackage()
    {
        using var inputs = new BuiltExtensionInputs("live-screenshot", "NEXUS_PACKAGED_SCREENSHOT_RECEIPT");
        using var execution = new ExecutionAdmissionFixture(); var source = execution.Source;
        var media = new NexusPipeline.Platform.Storage.Execution.ExecutionLiveMedia(source.Instance.InstanceId);
        using var installed = new InstallRuntimeFixture(catalog: execution.CreateCatalog(),
            portDependencies: new(null, null, null, media.Previews, media, media),
            originalManifest: inputs.Manifest, originalManifestBytes: inputs.ManifestBytes, packageArchive: inputs.Archive);
        try
        {
            Assert.Equal(File.ReadAllBytes(inputs.Archive), File.ReadAllBytes(installed.Package.Archive));
            var activation = await installed.Session().ApplyAsync(installed.Prepared, default);
            Assert.True(activation.IsEnabled); Assert.True(activation.IsInstalled);
            installed.Prepared.Dispose(); installed.Verified.Dispose(); installed.Root.Dispose();
            ExtensionProjectionPublication.PublishIfDirty(source.Instance, source.Database, source.Lease, new(),
                installed.Roots, execution.Resources, execution.Index);
            var runtime = installed.Activator.RequirePublishedRuntime("live-screenshot");
            var frontend = Assert.IsType<InstalledFrontend>(runtime.Frontend);
            var entry = await frontend.ReadAssetAsync("entry", default);
            Assert.Equal(File.ReadAllBytes(Path.Combine(installed.Target, "frontend/main.js")), entry);
            var style = await frontend.ReadAssetAsync("0", default);
            Assert.Equal(File.ReadAllBytes(Path.Combine(installed.Target, "frontend/main.css")), style);
            await Assert.ThrowsAsync<IOException>(() => frontend.CapabilityAsync("undeclared", default).AsTask());
            await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<Exception>(() => frontend.ReadAssetAsync("entry", default).AsTask());
            inputs.VerifyUnchanged();
        }
        finally { await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }
}
