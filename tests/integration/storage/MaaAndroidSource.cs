using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;
using AutomationTarget = NexusPipeline.Core.Automation.AutomationTarget;

namespace NexusPipeline.StorageTests;

internal static class MaaAndroidSource
{
    internal static AutomationTarget Prepare(ProjectSourceFixture source, Guid rootId)
    {
        var directory = Path.Combine(source.Path, "android-tool"); Directory.CreateDirectory(directory);
        var executable = Environment.GetEnvironmentVariable("NEXUS_ANDROID_TOOL_FIXTURE_EXE") ?? throw new IOException("android_tool_fixture_missing");
        var frozen = ExecutionHookImages.Capture(executable, runtime: true);
        var image = Path.Combine(directory, Path.GetFileName(executable)); File.Copy(executable, image);
        foreach (var file in frozen.RuntimeFiles)
        {
            var destination = Path.Combine(directory, file.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(Path.GetDirectoryName(executable)!, file.RelativePath), destination);
        }
        File.WriteAllBytes(Path.Combine(directory, "fixture-device.json"), JsonSerializer.SerializeToUtf8Bytes(new {
            driver = "genericAdb", endpoint = "127.0.0.1:16384", port = 16384, index = 0, package = "com.nexus.fixture",
            nonce = Guid.NewGuid().ToString("N"), online = true, foreground = "com.nexus.fixture"
        }));
        return new(AutomationTargetKind.Android, false, 10, false, null,
            new(AutomationAndroidDriver.GenericAdb, new(rootId, "android-tool/" + Path.GetFileName(image)),
                "127.0.0.1:16384", "com.nexus.fixture", ["-n", "com.nexus.fixture/.Main"]));
    }

    internal static void RequireClicks(ProjectSourceFixture source)
    {
        var invocations = Directory.GetFiles(Path.Combine(source.Path, "android-tool/observed"), "*.json")
            .Select(path => JsonSerializer.Deserialize<JsonElement>(File.ReadAllBytes(path))).ToArray();
        Assert.Equal(2, invocations.Count(value => value.GetProperty("arguments").EnumerateArray()
            .Any(argument => argument.GetString() == "input tap 1 1")));
    }
}
