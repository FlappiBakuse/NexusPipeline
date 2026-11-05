using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using NexusPipeline.Core.Automation;
using NexusPipeline.Host.Control.Http;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class EmulatorControlTests
{
    [Fact(DisplayName = "Actual Http And Resident CLI Share The Original Installed Provider Discovery And Reject Forged Support Flags And Stale Catalog Revisions")]
    public async Task UsesDiscoveryOwner()
    {
        using var fixture = new EmulatorRuntimeFixture("bluestacks");
        var source = fixture.Runtime.Source; var catalog = source.Definitions.Read();
        var builder = WebApplication.CreateSlimBuilder(); builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        var authority = new ControlAuthentication(source.Instance.InstanceId, false, null, source.Settings.Read, null!, TimeProvider.System);
        app.Use(ControlExceptionBoundary.InvokeAsync); app.Use(authority.InvokeAsync); new EmulatorControl(authority, fixture.Emulators, new()).Map(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(address) };
        var body = new JsonObject { ["format"] = "nexus.control.emulator-discover", ["version"] = 1, ["scriptId"] = source.ScriptId.ToString("D"),
            ["expectedCatalogRevision"] = catalog.Revision, ["rootIds"] = new JsonArray(fixture.Selected.Selection.RootId) };
        using (var response = await http.PostAsJsonAsync("/control/v1/automation/emulators/discover", body))
        {
            response.EnsureSuccessStatusCode(); using var result = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(catalog.Revision, result.RootElement.GetProperty("catalogRevision").GetInt64());
            Assert.Equal(fixture.Selected.Selection.Generation, Assert.Single(result.RootElement.GetProperty("targets").EnumerateArray()).GetProperty("selection").GetProperty("generation").GetString());
        }
        var input = Path.Combine(source.Root, "模拟器发现.json"); File.WriteAllText(input, body.ToJsonString());
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { "control", "emulator-discover", "--address", address, "--input", input }) start.ArgumentList.Add(value);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        Assert.Equal(0, process.ExitCode); Assert.Empty(await errors);
        using (var reply = JsonDocument.Parse(await output)) Assert.Equal(200, reply.RootElement.GetProperty("status").GetInt32());
        body["supportsAndroid"] = true;
        using (var invalid = await http.PostAsJsonAsync("/control/v1/automation/emulators/discover", body)) Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        body.Remove("supportsAndroid"); body["expectedCatalogRevision"] = catalog.Revision - 1;
        using (var conflict = await http.PostAsJsonAsync("/control/v1/automation/emulators/discover", body)) Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var script = catalog.Scripts.Single(value => value.Id == source.ScriptId);
        fixture.Emulators.RequireAndroid(script);
        Assert.Throws<IOException>(() => fixture.Emulators.RequireAndroid(script with { Kind = AutomationKind.Specialized, ExtensionId = "emulator-support" }));
    }
}
