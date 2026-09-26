using System.Collections.Concurrent;
using System.Text;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class ScriptProcessSessionTests
{
    [Fact]
    public void DeclaredLauncherWithExactOwnedIdentityIsRetainedAfterAutomationBoundary()
    {
        Assert.True(OperatingSystem.IsWindows());
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var script = new ScriptInstance { Name = "launcher probe", MainExe = powershell };
        using var session = ScriptProcessSession.Start(script, "测试", powershell, Path.GetTempPath(),
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 15"], null, null,
            ProcessRole.GameLauncher);
        try
        {
            Assert.NotNull(session.PreservedLauncher);
            Assert.True(session.Ownership?.HasAssignedProcess);
            var monitor = new AttemptMonitor();
            Assert.False(monitor.IsScriptExited(session.Process, powershell, session.Ownership,
                excludeGame: null, processSnapshot: null, preservedLauncher: session.PreservedLauncher,
                automationBoundaryConfirmed: true, knownRequiredWorkersStopped: false));
            Assert.False(monitor.IsScriptExited(session.Process, powershell, session.Ownership,
                excludeGame: null, processSnapshot: null, preservedLauncher: session.PreservedLauncher));
            bool exited = monitor.IsScriptExited(session.Process, powershell, session.Ownership,
                excludeGame: null, processSnapshot: null, preservedLauncher: session.PreservedLauncher,
                automationBoundaryConfirmed: true);
            ProcessObservation observation = session.Ownership!.Observe();
            Assert.True(exited, $"quality={observation.Quality} raw={string.Join(',', observation.RawPids)} "
                + $"identities={string.Join(';', observation.Identities)} preserved={session.PreservedLauncher} "
                + $"sysdir={Environment.SystemDirectory} sidecars={string.Join(',', observation.Identities.Select(ProcessRoleClassifier.IsConsoleSidecar))} "
                + $"rootMatches={string.Join(',', observation.Identities.Select(identity => session.PreservedLauncher!.Value.Matches(identity)))}");
            var finalizer = new RunAttemptFinalizer(script, "测试", () => null);
            Assert.True(session.KillAndConfirm(finalizer, excludeGame: null));
            Assert.False(session.Process.HasExited);
        }
        finally
        {
            if (!session.Process.HasExited)
            {
                session.Process.Kill();
                session.Process.WaitForExit(5000);
            }
        }
    }

    [Fact]
    public async Task RetainedLauncherDoesNotHideLiveAutomationChild()
    {
        Assert.True(OperatingSystem.IsWindows());
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        string command = "$child=Start-Process -FilePath '" + powershell
            + "' -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 3' "
            + "-NoNewWindow -PassThru; Start-Sleep -Seconds 15";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var script = new ScriptInstance { Name = "launcher child probe", MainExe = powershell };
        using var session = ScriptProcessSession.Start(script, "测试", powershell, Path.GetTempPath(),
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], null, null,
            ProcessRole.GameLauncher);
        try
        {
            Assert.NotNull(session.PreservedLauncher);
            var monitor = new AttemptMonitor();
            bool ChildAlive() => session.Ownership!.Observe().Identities.Any(identity =>
                identity.Pid != session.Process.Id
                && string.Equals(Path.GetFileName(identity.ImageName), "powershell.exe", StringComparison.OrdinalIgnoreCase));
            await SpinWaitAsync(ChildAlive, TimeSpan.FromSeconds(4));
            Assert.False(monitor.IsScriptExited(session.Process, powershell, session.Ownership,
                excludeGame: null, processSnapshot: null, preservedLauncher: session.PreservedLauncher));
            await SpinWaitAsync(() => !ChildAlive(), TimeSpan.FromSeconds(6));
            Assert.True(monitor.IsScriptExited(session.Process, powershell, session.Ownership,
                excludeGame: null, processSnapshot: null, preservedLauncher: session.PreservedLauncher));
            Assert.False(session.Process.HasExited);
        }
        finally
        {
            if (!session.Process.HasExited)
            {
                session.Process.Kill(entireProcessTree: true);
                session.Process.WaitForExit(5000);
            }
        }
    }

    private static async Task SpinWaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(condition(), "process observation did not reach the expected state");
    }

    [Theory]
    [InlineData("maaend")]
    [InlineData("maas")]
    public async Task MxuStdout_DecodesUtf8MultibyteAndFinalLineWithoutNewline(string pluginType)
    {
        Assert.True(OperatingSystem.IsWindows());
        const string expected = "任务启动失败: 未搜索到任何窗口";
        string command = "$b=[Text.Encoding]::UTF8.GetBytes('" + expected
            + "');[Console]::OpenStandardOutput().Write($b,0,$b.Length)";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var script = new ScriptInstance { Name = "UTF-8 output probe", PluginType = pluginType };
        var lines = new ConcurrentQueue<string>();
        using var session = ScriptProcessSession.Start(
            script,
            "测试",
            powershell,
            Path.GetTempPath(),
            new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded },
            null,
            null,
            outputEncoding: "utf-8");
        session.AttachOutput((line, _) =>
        {
            if (line is not null) lines.Enqueue(line);
        });
        await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await session.WaitForOutputDrainAsync(CancellationToken.None);
        Assert.Equal(0, session.Process.ExitCode);
        Assert.Contains(expected, lines);
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("windows-936")]
    public async Task DeclaredEncoding_DecodesSplitBytesCrLfTailAndStderr(string declaration)
    {
        Assert.True(OperatingSystem.IsWindows());
        string encodingExpression = declaration == "utf-8"
            ? "[Text.Encoding]::UTF8" : "[Text.Encoding]::GetEncoding(936)";
        string command = "$e=" + encodingExpression + ";"
            + "$o=[Console]::OpenStandardOutput();$b=$e.GetBytes('启动成功' + [char]13 + [char]10 + '尾行');"
            + "$o.Write($b,0,1);$o.Flush();Start-Sleep -Milliseconds 20;$o.Write($b,1,$b.Length-1);$o.Flush();"
            + "$r=[Console]::OpenStandardError();$x=$e.GetBytes('错误输出');$r.Write($x,0,1);$r.Flush();"
            + "Start-Sleep -Milliseconds 20;$r.Write($x,1,$x.Length-1);$r.Flush()";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var lines = new ConcurrentQueue<string>();
        using var session = ScriptProcessSession.Start(new ScriptInstance { Name = "encoding probe" },
            "测试", powershell, Path.GetTempPath(),
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], null, null,
            outputEncoding: declaration);
        session.AttachOutput((line, _) => { if (line is not null) lines.Enqueue(line); });
        await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await session.WaitForOutputDrainAsync(CancellationToken.None);
        Assert.Equal(0, session.Process.ExitCode);
        Assert.Contains("启动成功", lines);
        Assert.Contains("尾行", lines);
        // Windows PowerShell appends its own CLIXML stderr trailer when launched
        // with redirected pipes; the first line must still decode correctly.
        Assert.Contains(lines, line => line.StartsWith("错误输出", StringComparison.Ordinal));
    }
}
