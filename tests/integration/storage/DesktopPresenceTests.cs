using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Desktop;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class DesktopPresenceTests
{
    [Fact(DisplayName = "Browser Calls Cannot Create A Desktop Session And Installed Reads Retain The Actual Host Session Identity")]
    public async Task ReadsActualSession()
    {
        var presence = new DesktopPresence();
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend",
            portDependencies: new(null, null, null, null, null, null, Desktop: presence));
        var frontend = await FrontendInstalledTests.OpenAsync(fixture);
        try
        {
            async Task<JsonElement> Read(bool local)
            {
                var result = await frontend.InvokeAsync("desktop-session", null, "browser", local,
                    "{}"u8.ToArray(), () => { }, default);
                using var value = JsonDocument.Parse(result.Content);
                return value.RootElement.Clone();
            }
            Assert.Equal(JsonValueKind.Null, (await Read(true)).ValueKind);
            Assert.Equal(JsonValueKind.Null, (await Read(false)).ValueKind);
            var session = Guid.NewGuid();
            presence.Begin(session);
            Assert.Equal(session, (await Read(true)).GetGuid());
            Assert.Equal(session, (await Read(false)).GetGuid());
            presence.End(Guid.NewGuid());
            Assert.Equal(session, (await Read(false)).GetGuid());
            presence.End(session);
            Assert.Equal(JsonValueKind.Null, (await Read(true)).ValueKind);
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }
}
