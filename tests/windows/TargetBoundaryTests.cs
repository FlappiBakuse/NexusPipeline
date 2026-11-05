using System.Diagnostics;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class TargetBoundaryTests
{
    [Fact(DisplayName = "Stopping An Attempt Job Leaves An Independent Preexisting Ready Target Outside Its Cleanup Authority")]
    public async Task PreservesExternalTargets()
    {
        var input = Inputs();
        using var targetOutput = new OwnedProcessOutput(); using var targetJob = new OwnedWorkerJob();
        using var attemptOutput = new OwnedProcessOutput(); using var attemptJob = new OwnedWorkerJob();
        using var targetOut = new StreamReader(targetOutput.StandardOutput); using var targetError = new StreamReader(targetOutput.StandardError);
        using var attemptOut = new StreamReader(attemptOutput.StandardOutput); using var attemptError = new StreamReader(attemptOutput.StandardError);
        var targetReads = Task.WhenAll(targetOut.ReadToEndAsync(), targetError.ReadToEndAsync());
        var attemptReads = Task.WhenAll(attemptOut.ReadToEndAsync(), attemptError.ReadToEndAsync());
        try
        {
            var external = targetJob.Start(input.Fixture, ["process-output", "wait", input.Marker + "-target"], input.Directory,
                Guid.NewGuid(), Guid.NewGuid(), _ => { }, targetOutput);
            await Ready(input.Marker + "-target");
            var attempt = attemptJob.Start(input.Fixture, ["process-output", "wait", input.Marker + "-attempt"], input.Directory,
                Guid.NewGuid(), Guid.NewGuid(), _ => { }, attemptOutput);
            await Ready(input.Marker + "-attempt");
            var policy = new ExecutionTargetPolicy(ExecutionTargetKind.Pc, true, true);
            Assert.Equal(TargetLaunchAction.UseReady, TargetLifecyclePolicy.Launch(policy,
                ExecutionTargetReadiness.Ready, ExecutionTargetAuthority.ExternalReadinessOnly, null));
            Assert.Equal(TargetCleanupAction.PreserveExternal, TargetLifecyclePolicy.Cleanup(policy,
                ExecutionTargetAuthority.ExternalReadinessOnly, new("step", EngineOutcome.Failed, BusinessOutcome.Failed, "execution_main_failed"),
                ExecutionStopReason.Completed, ExecutionRunContinuation.Final).Application);
            Assert.True(await attemptJob.StopAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0u, attemptJob.GetAccounting().ActiveProcesses);
            Assert.Contains(external.ProcessId, targetJob.ActiveProcessIds());
            Assert.Null(await targetJob.AwaitRootAsync(external, TimeSpan.FromMilliseconds(50)));
            Assert.NotEqual(external.ProcessId, attempt.ProcessId);
            Assert.False(targetReads.IsCompleted); await attemptReads.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            Assert.True(await attemptJob.StopAsync(TimeSpan.FromSeconds(10)));
            Assert.True(await targetJob.StopAsync(TimeSpan.FromSeconds(10)));
            await Task.WhenAll(targetReads, attemptReads).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact(DisplayName = "Disposing The Current Kill On Close Job Cannot Be Used As A Living Target Retention Implementation")]
    public async Task RejectsLiveDisposal()
    {
        var input = Inputs();
        using var output = new OwnedProcessOutput(); using var job = new OwnedWorkerJob();
        using var stdout = new StreamReader(output.StandardOutput); using var stderr = new StreamReader(output.StandardError);
        var reads = Task.WhenAll(stdout.ReadToEndAsync(), stderr.ReadToEndAsync());
        var identity = job.Start(input.Fixture, ["process-output", "wait", input.Marker], input.Directory,
            Guid.NewGuid(), Guid.NewGuid(), _ => { }, output);
        await Ready(input.Marker);
        using var process = Process.GetProcessById(identity.ProcessId);
        Assert.False(process.HasExited);
        job.Dispose();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(process.HasExited); await reads.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task Ready(string marker)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(marker + ".output-ready") && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.True(File.Exists(marker + ".output-ready"));
    }

    private static (string Directory, string Fixture, string Marker) Inputs()
    {
        var root = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new InvalidOperationException("Missing test root");
        var fixture = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new InvalidOperationException("Missing fixture");
        var directory = Path.Combine(root, "target-boundary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        return (directory, fixture, Path.Combine(directory, "ready"));
    }
}
