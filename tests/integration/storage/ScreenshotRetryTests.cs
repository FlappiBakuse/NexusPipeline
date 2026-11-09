using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ScreenshotRetryTests
{
    [Fact(DisplayName = "Original Screenshot Zip Rejects Late Frames Across Actual Native Target Repetition")]
    public Task ReplacesRetriedPreview() => RunAsync(capture: true);

    [Fact(DisplayName = "Original Screenshot Zip Releases Unavailable Preview After Actual Host Restart Declines An Unproven Budget")]
    public Task ReconcilesInterruptedPreview() => RunAsync(capture: false);

    private static async Task RunAsync(bool capture)
    {
        using var inputs = new BuiltExtensionInputs("live-screenshot", "NEXUS_PACKAGED_SCREENSHOT_RECEIPT");
        await using var host = new FrontendHostFixture();
        StateDatabase? database = null; RuntimeInstance? instance = null;
        object? context = null;
        await host.StartAsync(inputs, controlledPackage: catalog =>
        {
            database = catalog.Database; instance = catalog.Instance;
            return Seed(catalog, capture, out context);
        }, interruptible: !capture);
        var output = Path.Combine(host.Root, "browser"); Directory.CreateDirectory(output);
        var request = Path.Combine(output, "input.json");
        File.WriteAllBytes(request, JsonSerializer.SerializeToUtf8Bytes(new { address = host.Address.AbsoluteUri,
            modules = Environment.GetEnvironmentVariable("NEXUS_BROWSER_MODULES"), output, context }));
        var script = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("NEXUS_SCREENSHOT_BROWSER_SCRIPT"))!, "screenshot-retry.mjs");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("NEXUS_BROWSER_NODE")!)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(script); start.ArgumentList.Add(request);
        start.Environment["NEXUS_BROWSER_BEARER"] = host.Token;
        start.Environment["TEMP"] = output; start.Environment["TMP"] = output;
        using var browser = Process.Start(start)!;
        var stdout = browser.StandardOutput.ReadToEndAsync(); var stderr = browser.StandardError.ReadToEndAsync();
        try
        {
            var timer = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(output, "restart-request")) && !browser.HasExited && timer.Elapsed < TimeSpan.FromSeconds(110))
                await Task.Delay(50);
            Assert.True(File.Exists(Path.Combine(output, "restart-request")), "native_preview_restart_barrier_missing");
            if (capture) File.WriteAllText(Path.Combine(host.Root, "retry-worker.fail"), "fail native first invocation");
            else
            {
                var address = host.Address;
                await host.InterruptAsync();
                await host.ColdRestartAsync(preservePort: true);
                Assert.Equal(address, host.Address);
                File.WriteAllText(Path.Combine(output, "host-restarted"), "ready");
            }
            await browser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(150));
        }
        finally
        {
            if (!browser.HasExited) { browser.Kill(entireProcessTree: true); await browser.WaitForExitAsync(); }
            File.WriteAllText(Path.Combine(output, "stdout.log"), await stdout);
            File.WriteAllText(Path.Combine(output, "stderr.log"), await stderr);
        }
        Assert.Equal(0, browser.ExitCode);
        using var result = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "result.json")));
        Assert.Equal("PASS", result.RootElement.GetProperty("status").GetString());
        Assert.NotEmpty(result.RootElement.GetProperty("checks").EnumerateArray());
        var attempts = result.RootElement.GetProperty("attempts").EnumerateArray().Select(item => item.GetGuid()).ToArray();
        Assert.Single(attempts.Distinct());
        var store = new AttemptReceiptStore(database!);
        var completed = Stopwatch.StartNew();
        while (store.Read(attempts[^1]) is null && completed.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(50);
        var receipts = attempts.Select(id => Assert.IsType<AttemptReceipt>(store.Read(id))).ToArray();
        Assert.Equal(capture ? LifecycleOutcome.Cancelled : LifecycleOutcome.Interrupted, receipts[^1].Facts.Lifecycle);
        if (capture)
        {
            var state = AttemptWriterState.Read(instance!, database!, new(), receipts[0].JournalId);
            Assert.Equal(2, state.AllJobs.Length);
        }
        else
        {
            Assert.Equal(1, receipts[0].Facts.Ordinal);
            using var connection = DatabaseConnections.Open(database!.DatabasePath, readOnly: true);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT reason_code FROM execution_retry_outcomes";
            Assert.Equal("retry_budget_unknown", command.ExecuteScalar());
            command.CommandText = "SELECT count(*) FROM attempt_receipts";
            Assert.Equal(1L, command.ExecuteScalar());
            command.CommandText = "SELECT count(*) FROM execution_start_sources";
            Assert.Equal(1L, command.ExecuteScalar());
        }
        inputs.VerifyUnchanged();
    }

    private static BoundPackageFixture Seed(ExtensionCatalogFixture catalog, bool capture, out object context)
    {
        var parent = Path.GetDirectoryName(catalog.Instance.RuntimeRoot)!;
        var marker = Path.Combine(parent, "retry-worker");
        using var provider = new ProviderPackageFixture(["provider-fixture-worker", capture ? "preview-repeat" : "retry-second-hold", marker], retrySafe: true);
        var managed = Environment.GetEnvironmentVariable("NEXUS_MANAGED_EXTENSION_FIXTURE_DLL")!;
        File.Copy(managed, Path.Combine(provider.Deployed, Path.GetFileName(managed)));
        var manifest = JsonNode.Parse(provider.ManifestBytes)!.AsObject();
        manifest["managed"] = new JsonObject { ["entryAssembly"] = Path.GetFileName(managed), ["entryType"] = "Controlled.RetryExtension" };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        File.WriteAllBytes(Path.Combine(provider.Deployed, "manifest.json"), bytes);
        var archive = Path.Combine(provider.Root, "installed.zip");
        ZipFile.CreateFromDirectory(provider.Deployed, archive, CompressionLevel.NoCompression, includeBaseDirectory: false);
        var initial = AutomationDefinitionSeed.Create(parent);
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Retry preview", AutomationRootKind.Script, provider.Deployed);
        var program = new AutomationPath(root.Id, Path.GetFileName(provider.Worker));
        var script = initial.Scripts[0] with
        {
            RootId = root.Id, Kind = AutomationKind.Specialized, ExtensionId = "provider-fixture", Executable = program,
            Arguments = [], Limits = new(2, 120, 300), Judgement = new(AutomationJudgementLanguage.Keywords, [], [], null),
            Target = capture ? new(AutomationTargetKind.Pc, true, 15, true,
                new(program, ["capture-window-fixture", marker + ".window", "retry-static-frame"], program, AutomationPcReadiness.VisibleWindow), null) : null
        };
        var binding = initial.Bindings[0] with { IsEnabled = true };
        var changes = initial with { Roots = [root], Scripts = [script], Bindings = [binding], Queues = [], Schedules = [] };
        new AutomationDefinitionStore(catalog.Database).Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "fixture", changes), changes, DateTimeOffset.UtcNow);
        var settings = new HostSettingsStore(catalog.Database); var before = settings.Read();
        var change = new HostSettingsChange(before.Revision, before.Values with { AutomaticRetry = new(true, 0) });
        settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "fixture", change), change, DateTimeOffset.UtcNow);
        context = new { bindingId = binding.Id, capture };
        return new(archive, manifest, bytes, catalog, ExtensionStoreChannel.Stable);
    }
}
