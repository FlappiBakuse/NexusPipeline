using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Scripts;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class ExecutionResourceScopeTests
{
    private static ScriptInstance Script(string id, string root, string mode, string endpoint) => new()
    {
        Id = id,
        RootPath = root,
        MainExe = @"C:\SharedRuntime\python.exe",
        ConfigPath = Path.Combine(root, "config.yaml"),
        GameMode = mode,
        GameExe = endpoint,
        LaunchGame = mode == "pc",
    };

    [Fact]
    public void DistinctAdbProjectsMayShareReadOnlyPythonButNotWritableRoots()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "resource-scopes-" + Guid.NewGuid().ToString("N"));
        var first = ExecutionResourceSetBuilder.Build([(ScriptId: "a", Script: Script("a", Path.Combine(baseDir, "a"), "emulator", "127.0.0.1:5555"))]);
        var second = ExecutionResourceSetBuilder.Build([(ScriptId: "b", Script: Script("b", Path.Combine(baseDir, "b"), "emulator", "127.0.0.1:5557"))]);
        Assert.Null(first.FindConflict(second));
        var sharedRoot = ExecutionResourceSetBuilder.Build([(ScriptId: "b", Script: Script("b", Path.Combine(baseDir, "a"), "emulator", "127.0.0.1:5557"))]);
        Assert.StartsWith("writable:", first.FindConflict(sharedRoot));
    }

    [Fact]
    public void PcSharesDesktopInputWhileIndependentAdbDoesNot()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "resource-scopes-" + Guid.NewGuid().ToString("N"));
        var first = ExecutionResourceSetBuilder.Build([(ScriptId: "a", Script: Script("a", Path.Combine(baseDir, "a"), "pc", ""))]);
        var second = ExecutionResourceSetBuilder.Build([(ScriptId: "b", Script: Script("b", Path.Combine(baseDir, "b"), "pc", ""))]);
        Assert.StartsWith("desktop-input:", first.FindConflict(second));
        var adb = ExecutionResourceSetBuilder.Build([(ScriptId: "c", Script: Script("c", Path.Combine(baseDir, "c"), "emulator", "127.0.0.1:5555"))]);
        Assert.Null(first.FindConflict(adb));
    }
}
