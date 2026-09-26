using System.Diagnostics;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.Tests.Platform;

public sealed class WindowPollingTests
{
    [Fact]
    public async Task MinimizePollingFinishesWhenExactTargetExitsAndDoesNotKeepThirtySecondWait()
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-t 127.0.0.1")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            ProcessIdentity identity = ProcessIdentity.Capture(process)!.Value;
            Task<bool> polling = ProcessWindows.MinimizeWindowAsync(identity, 30, CancellationToken.None);
            Assert.False(polling.IsCompleted);
            process.Kill(); await process.WaitForExitAsync();
            Assert.False(await polling.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(await ProcessWindows.MinimizeWindowAsync(identity, 30, CancellationToken.None));
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task MinimizePollingAcceptsCancellationWhileTargetIsAlive()
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-t 127.0.0.1")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            using var stop = new CancellationTokenSource();
            Task<bool> polling = ProcessWindows.MinimizeWindowAsync(ProcessIdentity.Capture(process)!.Value, 30, stop.Token);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => polling);
            Assert.False(process.HasExited);
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }
}
