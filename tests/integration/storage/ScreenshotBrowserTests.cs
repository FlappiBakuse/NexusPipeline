using System.Collections.Immutable;
using System.Text.Json;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ScreenshotBrowserTests
{
    [Fact(DisplayName = "Original Screenshot Zip And Production Browser Isolate Concurrent Native Windows And Release Hidden Or Retired Frames")]
    public Task CapturesConcurrentRuns() => CaptureAsync(false);

    [Fact(DisplayName = "Original Screenshot Zip And Production Browser Isolate Native Window And Android Runs And Verify Actual Device Shutdown")]
    public Task CapturesAndroidRuns() => CaptureAsync(true);

    private static async Task CaptureAsync(bool android)
    {
        using var inputs = new BuiltExtensionInputs("live-screenshot", "NEXUS_PACKAGED_SCREENSHOT_RECEIPT");
        object? context = null; RuntimeInstance? instance = null; StateDatabase? database = null;
        await FrontendBrowserTests.RunAsync(inputs, "NEXUS_SCREENSHOT_BROWSER_SCRIPT",
            seed: catalog => { instance = catalog.Instance; database = catalog.Database; context = Seed(catalog, android); },
            browserContext: () => context, timeoutSeconds: 300);
        using var result = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(instance!.RuntimeRoot)!, "browser", "result.json")));
        var androidClosures = 0;
        foreach (var run in result.RootElement.GetProperty("runs").EnumerateArray())
        {
            var receipt = Assert.IsType<AttemptReceipt>(new AttemptReceiptStore(database!).Read(run.GetProperty("attemptId").GetGuid()));
            Assert.Equal(LifecycleOutcome.Cancelled, receipt.Facts.Lifecycle);
            var state = AttemptWriterState.Read(instance, database!, new(), receipt.JournalId);
            foreach (var collected in AndroidCollectedCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256))
            {
                androidClosures++;
                Assert.Equal(TargetCleanupOutcome.Completed, collected.Cleanup.Outcome);
                Assert.Equal(TargetCleanupVerification.DeviceOffline, collected.Cleanup.Verification);
                Assert.Equal(2, collected.Cleanup.ConfirmationCommands.Length);
            }
        }
        Assert.Equal(android ? 1 : 0, androidClosures);
    }

    private static object Seed(ExtensionCatalogFixture catalog, bool android)
    {
        var parent = Path.GetDirectoryName(catalog.Instance.RuntimeRoot)!;
        var initial = AutomationDefinitionSeed.Create(parent);
        var executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new IOException("fixture_image_missing");
        var roots = new List<AutomationRoot>();
        var scripts = new List<AutomationScript>();
        var bindings = new List<AutomationBinding>();
        var markers = new List<string>();
        var dimensions = new List<int[]>(); var pixels = new List<int[]?>();
        for (var i = 0; i < 2; i++)
        {
            var directory = Path.Combine(parent, "preview-" + i);
            Directory.CreateDirectory(directory);
            CopyProgram(executable, directory);
            var root = new AutomationRoot(Guid.NewGuid(), 0, "Preview " + i, AutomationRootKind.Script, directory);
            var marker = Path.Combine(directory, "preview-hooks");
            var program = new AutomationPath(root.Id, Path.GetFileName(executable));
            var script = initial.Scripts[0] with
            {
                Id = Guid.NewGuid(), Name = "Preview " + i, RootId = root.Id, Executable = program,
                Arguments = ["execution-hook", "main", marker, "-", "0", "True"],
                Limits = new(1, 0, 240),
                Judgement = new(AutomationJudgementLanguage.Keywords, ["preview-complete"], [], null),
                Target = new(AutomationTargetKind.Pc, true, 15, true,
                    new(program, ["capture-window-fixture", Path.Combine(directory, "window"), i == 0 ? "static-frame" : "inverse-frame"], program,
                        AutomationPcReadiness.VisibleWindow), null)
            };
            if (android && i == 1)
            {
                var tool = Environment.GetEnvironmentVariable("NEXUS_ANDROID_TOOL_FIXTURE_EXE") ?? throw new IOException("android_tool_fixture_missing");
                var tools = Path.Combine(directory, "android"); Directory.CreateDirectory(tools); CopyProgram(tool, tools);
                File.WriteAllBytes(Path.Combine(tools, "fixture-device.json"), JsonSerializer.SerializeToUtf8Bytes(new
                {
                    driver = "genericAdb", endpoint = AndroidRuntimeFixture.Endpoint, port = 16384, index = 0,
                    package = AndroidRuntimeFixture.Package, nonce = Guid.NewGuid().ToString("N"), online = true,
                    foreground = (string?)null, screenshotMode = "rgb"
                }));
                script = script with { Target = new(AutomationTargetKind.Android, true, 15, true, null,
                    new(AutomationAndroidDriver.GenericAdb, new(root.Id, "android/" + Path.GetFileName(tool)),
                        AndroidRuntimeFixture.Endpoint, AndroidRuntimeFixture.Package, ["-n", AndroidRuntimeFixture.Package + "/.Main"])) };
            }
            dimensions.Add(android && i == 1 ? [3, 2] : [480, 360]);
            pixels.Add(android && i == 1 ? null : i == 0 ? [255, 0, 0] : [0, 0, 255]);
            roots.Add(root); scripts.Add(script); markers.Add(marker);
            bindings.Add(initial.Bindings[i] with { ScriptId = script.Id, IsEnabled = true });
        }
        var changes = initial with { Roots = roots.ToImmutableArray(), Scripts = scripts.ToImmutableArray(),
            Bindings = bindings.ToImmutableArray(), Queues = [], Schedules = [] };
        var store = new AutomationDefinitionStore(catalog.Database);
        store.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
        return new { bindings = bindings.Select(item => item.Id).ToArray(), markers, dimensions, pixels };
    }
    private static void CopyProgram(string executable, string directory)
    {
        var image = ExecutionHookImages.Capture(executable, runtime: true);
        File.Copy(executable, Path.Combine(directory, Path.GetFileName(executable)));
        foreach (var file in image.RuntimeFiles)
        {
            var target = Path.Combine(directory, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(Path.GetDirectoryName(executable)!, file.RelativePath), target);
        }
    }
}
