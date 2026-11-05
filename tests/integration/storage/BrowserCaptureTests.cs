using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Desktop;
using NexusPipeline.Core.Secrets;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class BrowserCaptureTests
{
    [Theory(DisplayName = "Actual Installed Browser Capture Requires Separate Manifest Login Origins And Returns Only Owned Nonserializable Drafts")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiresLoginPermission(bool granted)
    {
        using var catalog = new ExtensionCatalogFixture(); using var clients = new NativeClientSessions(catalog.Instance.InstanceId);
        await using var captures = new BrowserLoginSessions();
        using var frontendLife = new CancellationTokenSource(); using var nativeLife = new CancellationTokenSource();
        var native = new DesktopSessionIdentity(catalog.Instance.InstanceId, Guid.NewGuid(), Guid.NewGuid()); var client = Guid.NewGuid();
        clients.Bind(client, native, () => { }, frontendLife.Token, () => { }, nativeLife.Token); captures.Poll(native);
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend", catalog: catalog,
            portDependencies: new(null, null, null, null, null, null, NativeClients: clients, BrowserLogins: captures),
            managedManifestEdit: manifest => { if (granted) Grant(manifest); });
        var frontend = await FrontendInstalledTests.OpenAsync(fixture); var operation = Guid.NewGuid();
        try
        {
            var state = await frontend.InvokeAsync("native-state", null, "fixture", true, "{}"u8.ToArray(), () => { }, default,
                clientId: client, clientLifetime: frontendLife.Token);
            using var availability = JsonDocument.Parse(state.Content);
            Assert.True(availability.RootElement.GetProperty("IsBound").GetBoolean());
            Assert.Equal(granted, availability.RootElement.GetProperty("CanCapture").GetBoolean());
            var pending = frontend.InvokeAsync("native-capture", operation, "fixture", true, "{}"u8.ToArray(), () => { }, default,
                clientId: client, clientLifetime: frontendLife.Token).AsTask();
            if (!granted)
            { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pending); Assert.Empty(captures.Poll(native)); return; }
            var offer = await Offered(captures, native);
            Assert.Equal(client, offer.ClientId); Assert.Equal(operation, offer.OperationId); Assert.Equal("fixture-plugin", offer.ExtensionId);
            using var secret = new SecretValue("synthetic"); var values = new Dictionary<string, SecretValue> { ["token"] = secret };
            Assert.Throws<UnauthorizedAccessException>(() => captures.Complete(native, offer.CaptureId, Guid.NewGuid(), "completed", values));
            Assert.False(pending.IsCompleted);
            captures.Complete(native, offer.CaptureId, offer.Nonce, "completed", values);
            using var result = JsonDocument.Parse((await pending).Content);
            Assert.True(result.RootElement.GetProperty("valid").GetBoolean());
            Assert.True(result.RootElement.GetProperty("jsonDenied").GetBoolean());
            Assert.True(result.RootElement.GetProperty("disposed").GetBoolean());
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory(DisplayName = "Actual Installed Capture Holds Its SDK Borrow Until Cleanup Even After Request Client Or Activation Cancellation")]
    [InlineData("request")]
    [InlineData("frontend")]
    [InlineData("activation")]
    public async Task DrainsOwnedCapture(string ending)
    {
        using var catalog = new ExtensionCatalogFixture(); using var clients = new NativeClientSessions(catalog.Instance.InstanceId);
        await using var captures = new BrowserLoginSessions();
        using var frontendLife = new CancellationTokenSource(); using var nativeLife = new CancellationTokenSource(); using var request = new CancellationTokenSource();
        var native = new DesktopSessionIdentity(catalog.Instance.InstanceId, Guid.NewGuid(), Guid.NewGuid()); var client = Guid.NewGuid();
        clients.Bind(client, native, () => { }, frontendLife.Token, () => { }, nativeLife.Token); captures.Poll(native);
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend", catalog: catalog,
            portDependencies: new(null, null, null, null, null, null, NativeClients: clients, BrowserLogins: captures), managedManifestEdit: Grant);
        var frontend = await FrontendInstalledTests.OpenAsync(fixture); Task? stopping = null;
        try
        {
            var pending = frontend.InvokeAsync("native-capture", Guid.NewGuid(), "fixture", false, "{}"u8.ToArray(), () => { }, request.Token,
                clientId: client, clientLifetime: frontendLife.Token).AsTask();
            var offer = await Offered(captures, native);
            if (ending == "request") request.Cancel();
            else if (ending == "frontend") frontendLife.Cancel();
            else stopping = fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!Assert.Single(captures.Poll(native)).IsCanceled) await Task.Delay(10, timeout.Token);
            Assert.False(pending.IsCompleted); if (stopping is not null) Assert.False(stopping.IsCompleted);
            using var late = new SecretValue("synthetic");
            captures.Complete(native, offer.CaptureId, offer.Nonce, "completed", new Dictionary<string, SecretValue> { ["token"] = late });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            if (stopping is not null) await stopping;
            Assert.Empty(captures.Poll(native));
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    private static void Grant(JsonObject manifest)
    {
        manifest["permissions"]!["desktopAccess"] = new JsonArray("interactive-login");
        manifest["permissions"]!["loginOrigins"] = new JsonArray("https://login.example.test");
    }
    private static async Task<BrowserLoginOffer> Offered(BrowserLoginSessions captures, DesktopSessionIdentity native)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        { var offers = captures.Poll(native); if (offers.Count != 0) return Assert.Single(offers); await Task.Delay(10, timeout.Token); }
    }
}
