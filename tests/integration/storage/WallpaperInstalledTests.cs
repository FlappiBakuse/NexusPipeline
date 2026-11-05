using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Desktop;
using NexusPipeline.Platform.Media;
using NexusPipeline.Platform.Storage.Extensions;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class WallpaperInstalledTests
{
    [Fact(DisplayName = "The Original Wallpaper Zip Saves Actual Images Effects And Order With Deduplication Cas And Last Reference Retirement")]
    public async Task SavesInstalledWallpaper()
    {
        using var inputs = new BuiltExtensionInputs("custom-wallpaper", "NEXUS_PACKAGED_WALLPAPER_RECEIPT");
        using var execution = new ExecutionAdmissionFixture();
        using var installed = Fixture(inputs, execution);
        var frontend = await OpenAsync(installed, execution);
        try
        {
            var state = await Read(frontend);
            Assert.False(state.GetProperty("enabled").GetBoolean());
            Assert.Equal(0, Revision(state)); Assert.Empty(state.GetProperty("assets").EnumerateArray());
            var image = Image(10);
            state = await Upload(frontend, image);
            var asset = Assert.Single(state.GetProperty("assets").EnumerateArray());
            var id = asset.GetProperty("id").GetString()!;
            Assert.False(asset.TryGetProperty("palette", out _));
            Assert.Equal("controlled.png", asset.GetProperty("name").GetString());
            state = await Upload(frontend, image);
            Assert.Single(state.GetProperty("assets").EnumerateArray());
            state = await Invoke(frontend, "save-settings", new { revision = Revision(state), patch = new
            { enabled = true, selectedId = id, order = new[] { id }, effects = new
                { blurPx = 40, dimPercent = 80, surfaceTransparencyPercent = 50, secondaryTransparency = false } } }, Guid.NewGuid());
            Assert.Equal(id, state.GetProperty("currentId").GetString());
            Assert.True(state.GetProperty("effectiveEnabled").GetBoolean());
            Assert.Equal(50, state.GetProperty("effects").GetProperty("surfaceTransparencyPercent").GetInt32());
            var revision = Revision(state);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(frontend, "save-settings",
                new { revision = revision - 1, patch = new { enabled = false } }, Guid.NewGuid()));
            await Assert.ThrowsAsync<JsonException>(() => Invoke(frontend, "save-settings", new { revision, patch = new
            { effects = new { blurPx = 0, dimPercent = 20, surfaceTransparencyPercent = 51, secondaryTransparency = true } } }, Guid.NewGuid()));
            Assert.Equal(revision, Revision(await Read(frontend)));
            await Assert.ThrowsAsync<JsonException>(() => Invoke(frontend, "save-palette", new
                { id, palette = new Dictionary<string, string> { ["--accent"] = "url(http://invalid/)" } }, Guid.NewGuid()));
            state = await Invoke(frontend, "save-palette", new { id, palette = new Dictionary<string, string>
                { ["--accent"] = "hsl(200 50% 42%)", ["--mask"] = "rgba(4, 10, 20, .44)" } }, Guid.NewGuid());
            Assert.Equal("hsl(200 50% 42%)", Assert.Single(state.GetProperty("assets").EnumerateArray()).GetProperty("palette").GetProperty("--accent").GetString());
            var bytes = await frontend.ReadImageAsync("read-asset", id, "wallpaper:user", false, () => { }, default);
            Assert.Equal(image, bytes.Content.ToArray());
            state = await Invoke(frontend, "delete-asset", new { id }, Guid.NewGuid());
            Assert.Empty(state.GetProperty("assets").EnumerateArray());
            Assert.False(state.GetProperty("effectiveEnabled").GetBoolean());
            await Assert.ThrowsAsync<IOException>(() => frontend.ReadImageAsync("read-asset", id, "wallpaper:user", false, () => { }, default).AsTask());
            inputs.VerifyUnchanged();
        }
        finally { await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact(DisplayName = "The Actual Wallpaper Package Rotates Once Per Native Session And One Real Minute While Browser Reads Remain Stable")]
    public async Task RotatesInstalledWallpaper()
    {
        using var inputs = new BuiltExtensionInputs("custom-wallpaper", "NEXUS_PACKAGED_WALLPAPER_RECEIPT");
        using var execution = new ExecutionAdmissionFixture(); var desktop = new DesktopPresence();
        using var installed = Fixture(inputs, execution, desktop);
        var frontend = await OpenAsync(installed, execution);
        try
        {
            _ = await Upload(frontend, Image(20));
            var state = await Upload(frontend, Image(30));
            state = await Invoke(frontend, "save-settings", new { revision = Revision(state), patch = new
                { enabled = true, rotation = new { mode = "startup", intervalMinutes = 1 } } }, Guid.NewGuid());
            var before = state.GetProperty("currentId").GetString();
            Assert.Equal(before, (await Read(frontend)).GetProperty("currentId").GetString());
            var session = Guid.NewGuid(); desktop.Begin(session);
            var current = (await Read(frontend)).GetProperty("currentId").GetString();
            Assert.NotEqual(before, current);
            Assert.Equal(current, (await Read(frontend)).GetProperty("currentId").GetString());
            desktop.End(session);
            Assert.Equal(current, (await Read(frontend)).GetProperty("currentId").GetString());
            state = await Read(frontend);
            state = await Invoke(frontend, "save-settings", new { revision = Revision(state), patch = new
                { rotation = new { mode = "timer", intervalMinutes = 1 } } }, Guid.NewGuid());
            current = state.GetProperty("currentId").GetString();
            var next = state.GetProperty("rotation").GetProperty("nextSwitchAt").GetDateTimeOffset();
            Assert.InRange((next - DateTimeOffset.UtcNow).TotalSeconds, 55, 61);
            Assert.Equal(current, (await Read(frontend)).GetProperty("currentId").GetString());
            await Task.Delay(TimeSpan.FromSeconds(61));
            var later = await Read(frontend);
            Assert.NotEqual(current, later.GetProperty("currentId").GetString());
            Assert.Equal(Revision(state), Revision(later));
            inputs.VerifyUnchanged();
        }
        finally { await installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    private static InstallRuntimeFixture Fixture(BuiltExtensionInputs inputs, ExecutionAdmissionFixture execution,
        DesktopPresence? desktop = null) => new(catalog: execution.CreateCatalog(),
            portDependencies: new(null, null, null, null, null, null, Desktop: desktop),
            originalManifest: inputs.Manifest, originalManifestBytes: inputs.ManifestBytes, packageArchive: inputs.Archive);

    private static async Task<InstalledFrontend> OpenAsync(InstallRuntimeFixture installed, ExecutionAdmissionFixture execution)
    {
        var activation = await installed.Session().ApplyAsync(installed.Prepared, default);
        Assert.True(activation.IsInstalled); Assert.True(activation.IsEnabled);
        installed.Prepared.Dispose(); installed.Verified.Dispose(); installed.Root.Dispose();
        var source = execution.Source;
        ExtensionProjectionPublication.PublishIfDirty(source.Instance, source.Database, source.Lease, new(),
            installed.Roots, execution.Resources, execution.Index);
        return Assert.IsType<InstalledFrontend>(installed.Activator.RequirePublishedRuntime("custom-wallpaper").Frontend);
    }

    private static byte[] Image(byte color)
    {
        using var bytes = new MemoryStream();
        PngScanlineEncoder.Write(bytes, 1, 1, (_, row) => { row[0] = color; row[1] = 100; row[2] = 200; row[3] = 255; });
        return bytes.ToArray();
    }

    private static long Revision(JsonElement state) => state.GetProperty("revision").GetInt64();
    private static Task<JsonElement> Read(InstalledFrontend frontend) => Invoke(frontend, "read-state", new { });

    private static async Task<JsonElement> Upload(InstalledFrontend frontend, byte[] image)
    {
        var result = await frontend.InvokeAsync("upload-asset", Guid.NewGuid(), "wallpaper:user", false,
            image, () => { }, default, "image/png", "controlled.png");
        using var json = JsonDocument.Parse(result.Content);
        return json.RootElement.Clone();
    }

    private static async Task<JsonElement> Invoke(InstalledFrontend frontend, string operation, object input, Guid? id = null)
    {
        var result = await frontend.InvokeAsync(operation, id, "wallpaper:user", false,
            JsonSerializer.SerializeToUtf8Bytes(input), () => { }, default);
        using var json = JsonDocument.Parse(result.Content);
        return json.RootElement.Clone();
    }
}
