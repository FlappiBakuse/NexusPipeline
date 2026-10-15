using System.Diagnostics;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class ProcessCompletionTests
{
    [Fact]
    public async Task RetainedLauncherRequiresAnIdentifiedOwnedWorkerExit()
    {
        using var ownership = ProcessOwnership.TryCreate("retained launcher completion fixture");
        Assert.NotNull(ownership);
        Process Start() => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = "/d /q /c \"echo READY&set /p gate= & exit /b 0\"", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        using var launcher = Start(); using var worker = Start();
        using var helper = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            Arguments = "-NoProfile -NonInteractive -Command \"[Console]::WriteLine('READY'); [void][Console]::ReadLine(); exit 7\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        try
        {
            Assert.True(ownership.TryAssign(launcher)); Assert.True(ownership.TryAssign(worker));
            Assert.True(ownership.TryAssign(helper));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.Equal("READY", await launcher.StandardOutput.ReadLineAsync(timeout.Token));
            Assert.Equal("READY", await worker.StandardOutput.ReadLineAsync(timeout.Token));
            Assert.Equal("READY", await helper.StandardOutput.ReadLineAsync(timeout.Token));
            var running = ownership.ObserveCompletion();
            while (!running.Complete || !running.Processes.Any(item => item.Identity.Pid == worker.Id))
            { await Task.Delay(10, timeout.Token); running = ownership.ObserveCompletion(); }
            var root = Assert.Single(running.Processes, item => item.Identity.Pid == launcher.Id).Identity;
            var child = Assert.Single(running.Processes, item => item.Identity.Pid == worker.Id).Identity;
            var images = new HashSet<string>([child.ImageName], StringComparer.OrdinalIgnoreCase);
            Assert.Equal("unconfirmed", TaskExecutionCompletion.FromOwnedProcesses(running, root, ProcessRole.GameLauncher, null, images).Status);
            await worker.StandardInput.WriteLineAsync("continue"); await worker.StandardInput.FlushAsync();
            await helper.StandardInput.WriteLineAsync("continue"); await helper.StandardInput.FlushAsync();
            await worker.WaitForExitAsync(timeout.Token);
            await helper.WaitForExitAsync(timeout.Token);
            var ended = ownership.ObserveCompletion();
            while (ended.Processes.Single(item => item.Identity.Pid == worker.Id).ExitCode is null)
            { await Task.Delay(10, timeout.Token); ended = ownership.ObserveCompletion(); }
            Assert.False(launcher.HasExited);
            Assert.True(ended.Complete, System.Text.Json.JsonSerializer.Serialize(ended));
            Assert.Equal(7, ended.Processes.Single(item => item.Identity.Pid == helper.Id).ExitCode);
            Assert.Equal("normal", TaskExecutionCompletion.FromOwnedProcesses(ended, root, ProcessRole.GameLauncher, null, images).Status);
            Assert.Equal("unconfirmed", TaskExecutionCompletion.FromOwnedProcesses(ended, root, ProcessRole.GameLauncher, null,
                new HashSet<string>([Path.Combine(Path.GetTempPath(), "unowned-python.exe")])).Status);
            Assert.Equal("unconfirmed", TaskExecutionCompletion.FromOwnedProcesses(ended with { Complete = false }, root,
                ProcessRole.GameLauncher, null, images).Status);
        }
        finally
        {
            foreach (var process in new[] { launcher, worker, helper })
                if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
        }
    }

    [Fact]
    public async Task CompletionRequiresEveryOwnedExitAndRetainsNonzeroCodes()
    {
        foreach (var (code, child) in new[] { (0, false), (7, false), (259, false), (0, true) })
        {
            using var ownership = ProcessOwnership.TryCreate("completion fixture");
            Assert.NotNull(ownership);
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                Arguments = "/d /q /c \"set /p gate= & " + (child ? "cmd /d /c exit 0 & " : "") + "exit /b " + code + "\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            try
            {
                Assert.True(ownership.TryAssign(process));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var running = ownership.ObserveCompletion();
                while (!running.Complete || !running.Processes.Any(item => ProcessRoleClassifier.IsConsoleSidecar(item.Identity)))
                {
                    await Task.Delay(10, timeout.Token);
                    running = ownership.ObserveCompletion();
                }
                Assert.True(running.Complete);
                var root = Assert.Single(running.Processes, item => item.Identity.Pid == process.Id);
                Assert.Null(root.ExitCode);
                Assert.Equal("unconfirmed", TaskExecutionCompletion.FromOwnedProcesses(running, root.Identity, ProcessRole.AutomationWorker, null).Status);
                await process.StandardInput.WriteLineAsync("continue");
                await process.StandardInput.FlushAsync();
                await process.WaitForExitAsync(timeout.Token);
                while (!ownership.Observe().IsTrustworthyEmpty) await Task.Delay(10, timeout.Token);
                var completed = ownership.ObserveCompletion();
                Assert.True(completed.Complete == !child, System.Text.Json.JsonSerializer.Serialize(completed));
                Assert.Equal(code, Assert.Single(completed.Processes, item => item.Identity.Pid == process.Id).ExitCode);
                Assert.All(completed.Processes, item => Assert.NotNull(item.ExitCode));
                Assert.Equal(running.TotalProcesses + (child ? 1u : 0u), completed.TotalProcesses);
                var outcome = TaskExecutionCompletion.FromOwnedProcesses(completed, root.Identity, ProcessRole.AutomationWorker, null);
                Assert.Equal(child ? "unconfirmed" : code == 0 ? "normal" : "failed", outcome.Status);
                if (code == 0)
                    Assert.Equal("unconfirmed", TaskExecutionCompletion.FromOwnedProcesses(completed, root.Identity, ProcessRole.GameLauncher, null).Status);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
            }
        }
    }
}
