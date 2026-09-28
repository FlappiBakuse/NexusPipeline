using System.Diagnostics;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Tests.Support;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class GameReadinessTests
{
    [Theory]
    [InlineData("ready", 300)]
    [InlineData("timeout", 10000)]
    [InlineData("cancel", 10000)]
    public async Task ActualWindowReadinessWaitsWithinBudgetAndCancellationStopsWaiting(string scenario, int delay)
    {
        using var fixture = new ProviderWorkerPortTests.Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, ".nxp-test-fixture"), "owned-process-contract");
        string exe = Path.Combine(fixture.Root, "NexusPipeline.TestProviderWorker.exe");
        var script = new ScriptInstance { Name = scenario, LaunchGame = true, GameMode = "pc", GameExe = exe,
            GameArgs = "--owned-window " + delay, GameWaitSeconds = 2 };
        using var cancellation = new CancellationTokenSource();
        Process? game = null; ProcessIdentity? identity = null; int? selected = null;
        int? Ready()
        {
            if (game is null || game.HasExited) return null;
            game.Refresh();
            return game.MainWindowHandle != IntPtr.Zero ? game.Id : null;
        }
        var controller = new GameLaunchController(script, null, "test", () => cancellation.Token, () => 30, Ready,
            pid => selected = pid, () => null, new PluginAvailabilityPolicyTestsFixture.EmptyEmulatorSupportProviderResolver(),
            _ => { }, (_, _) => { }, null, captured =>
            {
                identity = captured; game = Process.GetProcessById(captured.Pid);
                Assert.Equal(captured, ProcessIdentity.Capture(game));
            });
        var watch = Stopwatch.StartNew();
        try
        {
            Task<RunAttemptResult?> run = controller.LaunchAsync();
            if (scenario == "cancel")
            {
                Assert.NotNull(game); Assert.Null(Ready()); cancellation.Cancel();
            }
            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(identity); Assert.NotNull(game); Assert.False(game.HasExited);
            if (scenario == "ready")
            {
                Assert.Null(result); Assert.Equal(game.Id, selected);
                string marker = Path.Combine(fixture.Root, "window-ready");
                var markerWait = Stopwatch.StartNew();
                while (!File.Exists(marker) && markerWait.Elapsed < TimeSpan.FromSeconds(1))
                    await Task.Delay(10);
                Assert.True(File.Exists(marker), "The fixture window never completed its Shown callback.");
                Assert.Equal("owned-window", File.ReadAllText(marker));
                Assert.InRange(watch.Elapsed.TotalSeconds, .25, 2.5);
                int pid = game.Id;
                var alreadyReady = Stopwatch.StartNew();
                Assert.Null(await controller.LaunchAsync());
                Assert.Equal(pid, game.Id); Assert.Equal(pid, selected);
                Assert.True(alreadyReady.Elapsed.TotalSeconds < 1);
            }
            else
            {
                Assert.NotNull(result); Assert.Null(selected);
                Assert.Equal(scenario == "cancel" ? "cancelled" : "failed", result.Status);
                if (scenario == "timeout")
                {
                    Assert.Equal("run.game_not_ready", result.ReasonCode);
                    Assert.InRange(watch.Elapsed.TotalSeconds, 1.8, 3.5);
                }
                else Assert.True(watch.Elapsed.TotalSeconds < 1.5);
                Assert.False(File.Exists(Path.Combine(fixture.Root, "window-ready")));
            }
        }
        finally
        {
            if (game is not null)
            {
                Assert.Equal(identity, ProcessIdentity.Capture(game));
                File.WriteAllText(Path.Combine(fixture.Root, "stop-window"), "owned test completed");
                await game.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                game.Dispose();
            }
        }
    }
}
