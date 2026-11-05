using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class ProviderAndroidFixture(ProviderRuntimeFixture runtime, string toolDirectory, bool mumu) : IAsyncDisposable
{
    internal ProviderRuntimeFixture Runtime { get; } = runtime;
    internal string ToolDirectory { get; } = toolDirectory;
    internal string ScreenshotPath => Path.Combine(ToolDirectory, mumu ? "paired-adb/fixture-screenshot.png" : "fixture-screenshot.png");

    internal static async Task<ProviderAndroidFixture> CreateAsync(bool cancel, bool mumu = false, string? fault = null)
    {
        var runtime = await ProviderRuntimeFixture.PrepareAsync(fault is not null ? "device-android-" + fault : cancel ? "device-android-cancel" : "device-android");
        try
        {
            var source = runtime.Source;
            var directory = Path.Combine(source.Root, "provider-android-tool"); Directory.CreateDirectory(directory);
            var executable = Environment.GetEnvironmentVariable("NEXUS_ANDROID_TOOL_FIXTURE_EXE") ?? throw new IOException("android_tool_fixture_missing");
            var image = CopyTool(executable, directory);
            if (mumu)
            {
                var paired = Path.Combine(directory, "paired-adb"); Directory.CreateDirectory(paired);
                _ = CopyTool(executable, paired);
                File.WriteAllBytes(Path.Combine(paired, "fixture-device.json"), JsonSerializer.SerializeToUtf8Bytes(new {
                    driver = "genericAdb", endpoint = "127.0.0.1:16384", port = 16384, index = 0, package = "com.nexus.fixture",
                    nonce = Guid.NewGuid().ToString("N"), online = true, foreground = "com.nexus.fixture"
                }));
            }
            File.WriteAllBytes(Path.Combine(directory, "fixture-device.json"), JsonSerializer.SerializeToUtf8Bytes(new {
                driver = mumu ? "mumu" : "genericAdb", endpoint = "127.0.0.1:16384", port = 16384, index = 0, package = "com.nexus.fixture",
                nonce = Guid.NewGuid().ToString("N"), online = true, foreground = "com.nexus.fixture",
                serverAction = cancel || fault == "timeout" ? "device-hang" : null,
                overflowAction = fault == "overflow" ? "device-output" : null
            }));
            var catalog = source.Definitions.Read();
            var root = new AutomationRoot(Guid.NewGuid(), 0, "Owned provider Android tool", AutomationRootKind.External, directory);
            var script = catalog.Scripts.Single(value => value.Id == source.OtherScriptId) with {
                Target = new(AutomationTargetKind.Android, false, 10, false, null,
                    new(mumu ? AutomationAndroidDriver.Mumu : AutomationAndroidDriver.GenericAdb, new(root.Id, Path.GetFileName(image)), "127.0.0.1:16384", "com.nexus.fixture", ["-n", "com.nexus.fixture/.Main"])
                    { ScreenshotTool = mumu ? new(root.Id, "paired-adb/" + Path.GetFileName(image)) : null })
            };
            var changes = new AutomationChanges(catalog.Revision, [root], [script], [], [], [], [], []);
            source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "provider-android-fixture", changes), changes, DateTimeOffset.UtcNow);
            if (mumu)
            {
                using var discovery = PreparedMuMuDiscovery.Prepare(source.Instance, new(), source.Lease, runtime.Base.Resources, image, "127.0.0.1:16384");
                await discovery.DiscoverAsync(TimeSpan.FromSeconds(10));
                runtime.Start(discovery);
            }
            else runtime.Start();
            if (fault == "mapping")
            {
                var path = Path.Combine(directory, "fixture-device.json");
                var device = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
                device["reportedPort"] = 16385;
                File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(device));
            }
            return new(runtime, directory, mumu);
        }
        catch { await runtime.DisposeAsync(); throw; }
    }

    private static string CopyTool(string executable, string directory)
    {
        var frozen = ExecutionHookImages.Capture(executable, runtime: true);
        var image = Path.Combine(directory, Path.GetFileName(executable)); File.Copy(executable, image);
        foreach (var file in frozen.RuntimeFiles)
        {
            var destination = Path.Combine(directory, file.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(Path.GetDirectoryName(executable)!, file.RelativePath), destination);
        }
        return image;
    }

    internal JsonElement[] Invocations() => Directory.GetFiles(Path.Combine(ToolDirectory, "observed"), "*.json")
        .Select(path => JsonSerializer.Deserialize<JsonElement>(File.ReadAllBytes(path))).ToArray();
    public ValueTask DisposeAsync() => Runtime.DisposeAsync();
}
