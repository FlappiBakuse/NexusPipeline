using System.Runtime.Versioning;
using System.Text.Json;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ActivationReadyTests
{
    [Theory(DisplayName = "Actual Installed Initialization Waits For Every Writer Borrow And Cancellation Cannot Publish It")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitsForWriter(bool cancel)
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "frontend-ready");
        try
        {
            var installed = await fixture.Session().ApplyAsync(fixture.Prepared, default);
            Assert.True(installed.IsEnabled);
            Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "readiness-started")));
            var complete = Path.Combine(fixture.ActivationDirectory, "readiness-complete.json");
            Assert.False(File.Exists(complete));
            if (cancel)
            {
                await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10));
                Assert.False(File.Exists(complete));
                Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "readiness-cancelled")));
            }
            else
            {
                fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!File.Exists(complete)) await Task.Delay(20, deadline.Token);
                using var result = JsonDocument.Parse(File.ReadAllBytes(complete));
                Assert.Equal(0, result.RootElement.GetProperty("revision").GetInt64());
            }
        }
        finally { await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
    }
}
