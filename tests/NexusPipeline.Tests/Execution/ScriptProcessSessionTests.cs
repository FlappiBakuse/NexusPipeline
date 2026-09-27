using System.Collections.Concurrent;
using System.Text;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Tests.Support;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class ScriptProcessSessionTests
{
    [Fact]
    public async Task CoordinatorPreservesProvedSuccessWhenRetainedLauncherKeepsStdoutOpen()
    {
        string root = Path.Combine(Path.GetTempPath(), "launcher-result-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        string command = "$PID | Set-Content -LiteralPath '" + Path.Combine(root, "pid.txt")
            + "'; Start-Process -FilePath $env:ComSpec -ArgumentList '/d','/c','ping -n 3 127.0.0.1 >nul' -NoNewWindow | Out-Null; "
            + "Set-Content -LiteralPath '" + Path.Combine(root, "run.log") + "' -Value 'business-completed'; Write-Output 'business-completed'; Start-Sleep -Seconds 60";
        var script = new ScriptInstance { Id = Path.GetFileName(root), Name = "retained launcher success", RootPath = root,
            MainExe = powershell, Args = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command)),
            LogPath = Path.Combine(root, "run.log"), SuccessKeywords = "business-completed", MaxAttempts = 1 };
        var spec = new ResolvedScriptSpec(script, "1", new(false, "javascript", "fixture", "", ""), "fixture")
            { RootProcessRole = ProcessRole.GameLauncher };
        var user = new ResolvedScriptUser("fixture", "Fixture", new() { ScriptInstanceId = script.Id, Enabled = true });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var coordinator = new ExecutionCoordinator(script, "manual", "", "", "Fixture", stop.Token, null, null, null, null,
            new PluginAvailabilityPolicyTestsFixture.EmptyUserRepository(), new PluginAvailabilityPolicyTestsFixture.EmptyEmulatorSupportProviderResolver(), user, spec);
        ProcessIdentity? launcher = null;
        System.Diagnostics.Process? launcherProcess = null;
        try
        {
            Task<NexusPipeline.Modules.History.RunRecord> run = coordinator.RunAsync();
            await SpinWaitAsync(() => File.Exists(Path.Combine(root, "pid.txt")), TimeSpan.FromSeconds(5));
            launcherProcess = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(root, "pid.txt"))));
            launcher = ProcessIdentity.Capture(launcherProcess);
            var record = await run;
            Assert.Equal("success", record.Status);
            Assert.True(Assert.Single(record.AttemptDetails).OutputIncomplete);
            Assert.Equal("success", record.AttemptDetails[0].Status);
            Assert.NotNull(launcher);
            Assert.Equal(launcher, ProcessIdentity.Capture(launcherProcess));
            Assert.Null(NexusPipeline.Modules.Configuration.Recovery.ConfigSessionMark.TryRead(script.Id, user.UserId));
        }
        finally
        {
            stop.Cancel();
            coordinator.DisposeScreenshots();
            if (launcher is { } identity && launcherProcess is not null && !launcherProcess.HasExited)
            {
                using var process = System.Diagnostics.Process.GetProcessById(identity.Pid);
                Assert.Equal(identity, ProcessIdentity.Capture(process));
                process.Kill(); Assert.True(process.WaitForExit(5000));
            }
            launcherProcess?.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ExitedRootDoesNotReleaseDifferentImageWriterBeforeOwnedCleanup()
    {
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        string command = "Start-Process -FilePath $env:ComSpec -ArgumentList '/d','/c','ping -n 30 127.0.0.1 >nul' -NoNewWindow | Out-Null";
        using var session = ScriptProcessSession.Start(new() { Name = "different image writer" }, "test", powershell,
            Path.GetTempPath(), ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command))], null, null);
        session.AttachOutput((_, _) => { });
        await SpinWaitAsync(() => session.Process.HasExited, TimeSpan.FromSeconds(10));
        var observation = session.Ownership!.Observe();
        Assert.Contains(observation.Identities, identity => Path.GetFileName(identity.ImageName).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase));
        var monitor = new AttemptMonitor();
        Assert.False(monitor.IsScriptExited(session.Process, powershell, session.Ownership, null, null));
        Assert.True(session.KillAndConfirm(new RunAttemptFinalizer(new() { Name = "different image writer" }, "test", () => null), null));
        Assert.True(session.Ownership.Observe().IsTrustworthyEmpty);
        Assert.True(monitor.IsScriptExited(session.Process, powershell, session.Ownership, null, null));
        await session.WaitForOutputDrainAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RetainedLauncherHoldingOutputHasBoundedIncompleteDrain()
    {
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        using var session = ScriptProcessSession.Start(new() { Name = "output holder" }, "test", powershell,
            Path.GetTempPath(), ["-NoProfile", "-NonInteractive", "-Command", "Write-Output 'business-completed'; Start-Sleep -Seconds 60"],
            null, null, ProcessRole.GameLauncher);
        var lines = new ConcurrentQueue<string>();
        session.AttachOutput((line, _) => { if (line is not null) lines.Enqueue(line); });
        try
        {
            await SpinWaitAsync(() => lines.Contains("business-completed"), TimeSpan.FromSeconds(5));
            Assert.True(new AttemptMonitor().IsScriptExited(session.Process, powershell, session.Ownership,
                null, null, session.PreservedLauncher, automationBoundaryConfirmed: true));
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await session.WaitForOutputDrainAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(13));
            Assert.InRange(timer.Elapsed.TotalSeconds, 9, 13);
            Assert.True(session.OutputIncomplete);
            Assert.False(session.Process.HasExited);
            Assert.Contains("business-completed", lines);
        }
        finally
        {
            if (!session.Process.HasExited) { session.Process.Kill(); session.Process.WaitForExit(5000); }
        }
    }

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
