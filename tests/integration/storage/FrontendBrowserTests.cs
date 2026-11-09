using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class FrontendBrowserTests
{
    [Fact(DisplayName = "The Actual Host App Loads The Authenticated Built Module Under Its Csp And Retires It After Disable")]
    public async Task LoadsAuthenticatedModule()
    {
        using var inputs = new BuiltExtensionInputs("live-screenshot", "NEXUS_PACKAGED_SCREENSHOT_RECEIPT");
        await RunAsync(inputs, "NEXUS_BROWSER_SCRIPT", remoteAccess: true);
    }

    [Fact(DisplayName = "The Actual Wallpaper Package Uploads And Reads Three Browser Image Formats Through The Authenticated App")]
    public async Task UsesInstalledWallpaper()
    {
        using var inputs = new BuiltExtensionInputs("custom-wallpaper", "NEXUS_PACKAGED_WALLPAPER_RECEIPT");
        await RunAsync(inputs, "NEXUS_WALLPAPER_BROWSER_SCRIPT", remoteAccess: true);
    }

    [Fact(DisplayName = "The Actual Activities Package Saves Browser Subscriptions And Shows Source Details Across Activation Retirement")]
    public async Task UsesInstalledActivities()
    {
        using var inputs = new BuiltExtensionInputs("game-activities", "NEXUS_PACKAGED_ACTIVITIES_RECEIPT");
        await RunAsync(inputs, "NEXUS_ACTIVITIES_BROWSER_SCRIPT", remoteAccess: true);
    }

    [Fact(DisplayName = "The Actual CheckIn Package Edits Private Tasks Over Authenticated Lan With No Outbound Requests Or Implicit Reveal")]
    public async Task UsesCheckIn()
    {
        using var inputs = new BuiltExtensionInputs("game-checkin", "NEXUS_PACKAGED_CHECKIN_RECEIPT");
        await RunAsync(inputs, "NEXUS_CHECKIN_BROWSER_SCRIPT", denyNetwork: true, remoteAccess: true);
    }

    [Fact(DisplayName = "Local Original Wallpaper Package Completes Production Upload And Lifecycle Without A LAN Listener")]
    public async Task UsesLocalWallpaper()
    {
        using var inputs = new BuiltExtensionInputs("custom-wallpaper", "NEXUS_PACKAGED_WALLPAPER_RECEIPT");
        await RunAsync(inputs, "NEXUS_WALLPAPER_BROWSER_SCRIPT", browserContext: () => new { localOnly = true }, timeoutSeconds: 150);
    }

    [Fact(DisplayName = "Local Original Activities Package Stops Hidden Reads And Preserves Production Subscription Lifecycle")]
    public async Task UsesLocalActivities()
    {
        using var inputs = new BuiltExtensionInputs("game-activities", "NEXUS_PACKAGED_ACTIVITIES_RECEIPT");
        await RunAsync(inputs, "NEXUS_ACTIVITIES_BROWSER_SCRIPT", browserContext: () => new { localOnly = true }, timeoutSeconds: 150);
    }

    [Fact(DisplayName = "Local Original CheckIn Package Edits Disabled Tasks Without A LAN Listener Or Outbound Requests")]
    public async Task UsesLocalTasks()
    {
        using var inputs = new BuiltExtensionInputs("game-checkin", "NEXUS_PACKAGED_CHECKIN_RECEIPT");
        await RunAsync(inputs, "NEXUS_CHECKIN_BROWSER_SCRIPT", denyNetwork: true,
            browserContext: () => new { localOnly = true }, timeoutSeconds: 150);
    }

    internal static async Task RunAsync(BuiltExtensionInputs inputs, string scriptVariable, bool denyNetwork = false,
        IReadOnlyList<BuiltExtensionInputs>? additional = null, Action<ExtensionCatalogFixture>? seed = null, Func<object?>? browserContext = null,
        int timeoutSeconds = 90, bool remoteAccess = false,
        Func<ExtensionCatalogFixture, BoundPackageFixture>? controlledPackage = null, string? application = null)
    {
        await using var proxy = denyNetwork ? new RejectingProxy() : null;
        await using var host = new FrontendHostFixture(); await host.StartAsync(inputs, proxy?.Address, additional, seed,
            application: application, remoteAccess: remoteAccess, controlledPackage: controlledPackage);
        var node = Environment.GetEnvironmentVariable("NEXUS_BROWSER_NODE") ?? throw new IOException("NEXUS_BROWSER_NODE_required");
        var script = Environment.GetEnvironmentVariable(scriptVariable) ?? throw new IOException(scriptVariable + "_required");
        var modules = Environment.GetEnvironmentVariable("NEXUS_BROWSER_MODULES") ?? throw new IOException("NEXUS_BROWSER_MODULES_required");
        var executable = Environment.GetEnvironmentVariable("NEXUS_TEST_BROWSER_EXE") ?? throw new IOException("NEXUS_TEST_BROWSER_EXE_required");
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new IOException("explicit_browser_required");
        var output = Path.Combine(host.Root, "browser"); Directory.CreateDirectory(output);
        var request = Path.Combine(output, "input.json");
        File.WriteAllBytes(request, JsonSerializer.SerializeToUtf8Bytes(new { address = host.Address.AbsoluteUri, modules, output,
            browser = executable, tokenEnvironment = "NEXUS_BROWSER_BEARER", context = browserContext?.Invoke() }));
        var start = new ProcessStartInfo(node) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(script); start.ArgumentList.Add(request);
        start.Environment["NEXUS_BROWSER_BEARER"] = host.Token;
        start.Environment["TEMP"] = output; start.Environment["TMP"] = output;
        using var browser = Process.Start(start)!;
        var stdout = browser.StandardOutput.ReadToEndAsync(); var stderr = browser.StandardError.ReadToEndAsync();
        try { await browser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds)); }
        finally { if (!browser.HasExited) { browser.Kill(entireProcessTree: true); await browser.WaitForExitAsync(); } }
        File.WriteAllText(Path.Combine(output, "stdout.log"), await stdout);
        File.WriteAllText(Path.Combine(output, "stderr.log"), await stderr);
        Assert.True(browser.ExitCode == 0, await stderr);
        using var result = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "result.json")));
        Assert.Equal("PASS", result.RootElement.GetProperty("status").GetString());
        Assert.NotEmpty(result.RootElement.GetProperty("checks").EnumerateArray());
        if (proxy is not null)
        {
            Assert.Equal(0, proxy.Connections);
            File.WriteAllBytes(Path.Combine(output, "outbound-proof.json"), JsonSerializer.SerializeToUtf8Bytes(new
                { status = "PASS", connections = proxy.Connections, scope = "Actual Host saved HTTP proxy rejects outbound traffic; no connection observed" }));
        }
        inputs.VerifyUnchanged();
        foreach (var input in additional ?? []) input.VerifyUnchanged();
    }
}
