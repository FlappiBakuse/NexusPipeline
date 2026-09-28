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
    public void WritableAncestorCaseAndDirectoryJunctionAreOneConflictScope()
    {
        string root = Path.Combine(Path.GetTempPath(), "resource-alias-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "physical");
        string alias = Path.Combine(root, "alias");
        Directory.CreateDirectory(Path.Combine(target, "child"));
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec")!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                Arguments = $"/d /c mklink /J \"{alias}\" \"{target}\"",
            };
            using var link = System.Diagnostics.Process.Start(start)!;
            Assert.True(link.WaitForExit(5000)); Assert.Equal(0, link.ExitCode);
            var owner = ExecutionResourceSetBuilder.Build([(ScriptId: "a", Script: Script("a", target, "emulator", "device-a"))]);
            foreach (string candidate in new[] { target.ToUpperInvariant(), Path.Combine(target, "child"), Path.Combine(alias, "child") })
            {
                var contender = ExecutionResourceSetBuilder.Build([(ScriptId: "b", Script: Script("b", candidate, "emulator", "device-b"))]);
                Assert.StartsWith("writable:", owner.FindConflict(contender));
            }
        }
        finally
        {
            if (Directory.Exists(alias)) Directory.Delete(alias);
            Directory.Delete(root, true);
        }
    }

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
