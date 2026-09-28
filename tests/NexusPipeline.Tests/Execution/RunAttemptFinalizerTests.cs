using System.Diagnostics;
using System.Text.Json;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class RunAttemptFinalizerTests
{
    [Theory]
    [InlineData("failed", false)]
    [InlineData("cancelled", true)]
    [InlineData("success", true)]
    public async Task StopsOnlyCapturedConfiguredGameAndPreservesOtherInstallationAndPreexistingInstance(string status, bool forceClose)
    {
        using var owned = new ProviderWorkerPortTests.Fixture(keepEvidence: true);
        using var another = new ProviderWorkerPortTests.Fixture(keepEvidence: true);
        foreach (string directory in new[] { owned.Root, another.Root })
            File.WriteAllText(Path.Combine(directory, ".nxp-test-fixture"), "owned-process-contract");
        string configured = Path.Combine(owned.Root, "NexusPipeline.TestProviderWorker.exe"), other = Path.Combine(another.Root, "NexusPipeline.TestProviderWorker.exe");
        async Task<Process> Start(string image)
        {
            Process process = Process.Start(new ProcessStartInfo(image, "--owned-lifetime")
                { WorkingDirectory = Path.GetDirectoryName(image), UseShellExecute = false, CreateNoWindow = true,
                  RedirectStandardOutput = true, RedirectStandardInput = true })!;
            Assert.Equal("owned-ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            return process;
        }
        using Process preexisting = await Start(configured), unrelated = await Start(other), target = await Start(configured);
        try
        {
            var finalizer = new RunAttemptFinalizer(new ScriptInstance { GameExe = configured, ForceCloseGame = forceClose }, "test", () => null);
            finalizer.TrackGameIdentity(ProcessIdentity.Capture(unrelated)!.Value);
            finalizer.TrackGameIdentity(ProcessIdentity.Capture(target)!.Value);
            var result = status switch { "failed" => RunAttemptResult.Failed("fixture"),
                "cancelled" => RunAttemptResult.Cancelled("fixture"), _ => RunAttemptResult.Success("fixture") };
            await finalizer.CleanupGameAsync(result, 1, 1);
            await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            File.WriteAllText(Path.Combine(owned.Root, "game-identity-evidence.json"), JsonSerializer.Serialize(new {
                status, forceClose, configured, other, targetPid = target.Id, targetExited = target.HasExited,
                preexistingPid = preexisting.Id, preexistingAlive = !preexisting.HasExited,
                otherPid = unrelated.Id, otherAlive = !unrelated.HasExited,
            }));
            Assert.False(preexisting.HasExited);
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            foreach (Process process in new[] { preexisting, unrelated, target })
                if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }
    }
}
