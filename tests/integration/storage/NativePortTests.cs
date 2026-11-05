using System.Runtime.Versioning;
using NexusPipeline.Core.Desktop;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class NativePortTests
{
    [Theory(DisplayName = "Actual Native Window Observations Require The Declared Inventory Permission And Current Original Native Client")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsNativeWindows(bool granted)
    {
        using var catalog = new ExtensionCatalogFixture(); using var clients = new NativeClientSessions(catalog.Instance.InstanceId);
        using var frontendLifetime = new CancellationTokenSource(); using var nativeLifetime = new CancellationTokenSource();
        using var window = new ProbeWindowFixture();
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend", catalog: catalog,
            portDependencies: new(null, null, null, null, null, null, NativeClients: clients),
            managedManifestEdit: manifest => {
                if (granted) manifest["permissions"]!["desktopAccess"] = new System.Text.Json.Nodes.JsonArray("window-inventory");
            });
        var frontend = await FrontendInstalledTests.OpenAsync(fixture); var client = Guid.NewGuid();
        var desktop = new NexusPipeline.Core.Desktop.DesktopSession(catalog.Instance.InstanceId, Guid.NewGuid());
        clients.Bind(client, desktop.Identity, () => { }, frontendLifetime.Token, () => desktop.RequireCurrent(desktop.Identity), nativeLifetime.Token);
        Task<NexusPipeline.Platform.Storage.Extensions.FrontendContent> Read(Guid id) => frontend.InvokeAsync("native-windows", Guid.NewGuid(),
            "controlled:frontend", false, "{}"u8.ToArray(), () => { }, default, clientId: id, clientLifetime: frontendLifetime.Token).AsTask();
        try
        {
            if (!granted) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Read(client)); return; }
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Read(Guid.NewGuid()));
            using var result = System.Text.Json.JsonDocument.Parse((await Read(client)).Content);
            var actual = result.RootElement.EnumerateArray().Single(row => row.GetProperty("Title").GetString() == window.Title);
            Assert.Equal(Environment.ProcessId, actual.GetProperty("ProcessId").GetInt32());
            Assert.Equal(Environment.ProcessPath, actual.GetProperty("Executable").GetString());
            Assert.True(actual.GetProperty("Handle").GetInt64() > 0);
            Assert.Equal(TimeSpan.Zero, actual.GetProperty("StartedAtUtc").GetDateTimeOffset().Offset);
            nativeLifetime.Cancel();
            var denied = await Record.ExceptionAsync(() => Read(client));
            Assert.True(denied is UnauthorizedAccessException or OperationCanceledException);
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory(DisplayName = "An Actual Installed SDK Native Call Requires The Original Host Bound Frontend Grant And Desktop Access Independent Of IsLocal Or Payload")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiresBoundClient(bool isLocal)
    {
        using var catalog = new ExtensionCatalogFixture(); using var clients = new NativeClientSessions(catalog.Instance.InstanceId);
        using var frontendLifetime = new CancellationTokenSource(); using var nativeLifetime = new CancellationTokenSource();
        var presence = new DesktopPresence(); presence.Begin(Guid.NewGuid());
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend", catalog: catalog,
            portDependencies: new(null, null, null, null, null, null, Desktop: presence, NativeClients: clients));
        var frontend = await FrontendInstalledTests.OpenAsync(fixture); var actualClient = Guid.NewGuid(); var live = true;
        var desktop = new NexusPipeline.Core.Desktop.DesktopSession(catalog.Instance.InstanceId, Guid.NewGuid());
        try
        {
            async Task<bool> Available(Guid client)
            {
                var reply = await frontend.InvokeAsync("native-state", null, "controlled:frontend", isLocal, "{}"u8.ToArray(), () => { }, default,
                    clientId: client, clientLifetime: frontendLifetime.Token);
                using var state = System.Text.Json.JsonDocument.Parse(reply.Content);
                Assert.False(state.RootElement.GetProperty("CanCapture").GetBoolean());
                return state.RootElement.GetProperty("IsBound").GetBoolean();
            }
            Task Call(Guid id, string operation = "native-require") => frontend.InvokeAsync(operation, operation == "native-read" ? null : Guid.NewGuid(),
                "controlled:frontend", isLocal, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { clientId = actualClient, native = true }),
                () => { }, default, clientId: id, clientLifetime: frontendLifetime.Token).AsTask();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Call(actualClient));
            Assert.False(await Available(actualClient));
            clients.Bind(actualClient, desktop.Identity, () => { }, frontendLifetime.Token,
                () => { if (!live) throw new UnauthorizedAccessException(); desktop.RequireCurrent(desktop.Identity); }, nativeLifetime.Token);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Call(Guid.NewGuid()));
            await Call(actualClient);
            Assert.True(await Available(actualClient)); Assert.False(await Available(Guid.NewGuid()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Call(actualClient, "native-read"));
            live = false; await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Call(actualClient));
            live = true; await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Call(actualClient));
            Assert.False(await Available(actualClient));
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }
}
