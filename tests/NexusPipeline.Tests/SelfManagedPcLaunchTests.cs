using NexusPipeline.Extensibility;
using NexusPipeline.Models;
using NexusPipeline.Services.Execution;
using Xunit;

namespace NexusPipeline.Tests;

public sealed class SelfManagedPcLaunchTests
{
    [Fact]
    public void SelfManagedPcLaunch_SuppressesOnlyPcHostLaunchAndPreservesDormantSettings()
    {
        var script = new ScriptInstance
        {
            GameMode = "pc",
            LaunchGame = true,
            GameExe = "C:\\Games\\game.exe",
            GameArgs = "--profile saved-user",
            GameWaitSeconds = 47,
        };
        var spec = new ResolvedScriptSpec(
            script.Clone(),
            "0.1.0",
            new ResolvedJudgeScript(false, "javascript", "", "", ""),
            "")
        {
            SelfManagedPcLaunch = true,
        };

        Assert.False(ExecutionCoordinator.ShouldHostLaunchGame(script, spec));
        Assert.Equal("C:\\Games\\game.exe", script.GameExe);
        Assert.True(script.LaunchGame);
        Assert.Equal("--profile saved-user", script.GameArgs);
        Assert.Equal(47, script.GameWaitSeconds);

        script.GameMode = "emulator";
        Assert.True(ExecutionCoordinator.ShouldHostLaunchGame(script, spec));
        Assert.True(script.LaunchGame);
        Assert.Equal("--profile saved-user", script.GameArgs);
        Assert.Equal(47, script.GameWaitSeconds);
    }
}
