using System.Diagnostics;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class AndroidRootTests
{
    [Fact(DisplayName = "An Exited Root Can Supply Its Actual Exit Code While Its Original Job Still Owns A Living Child")]
    public async Task ObservesExitedRoot()
    {
        var (directory, executable, marker) = Inputs();
        using var job = new OwnedWorkerJob(); using var output = new OwnedProcessOutput();
        using var stdout = new StreamReader(output.StandardOutput); using var stderr = new StreamReader(output.StandardError);
        var reads = Task.WhenAll(stdout.ReadToEndAsync(), stderr.ReadToEndAsync());
        try
        {
            var root = job.Start(executable, ["process-output", "child-root", marker], directory,
                Guid.NewGuid(), Guid.NewGuid(), _ => { }, output);
            Assert.Equal(0, await job.AwaitRootAsync(root, TimeSpan.FromSeconds(15)));
            var child = int.Parse(await File.ReadAllTextAsync(marker + ".child"));
            var actual = job.UseExitedRoot(root, code =>
            {
                Assert.Contains(child, job.ActiveProcessIds());
                Assert.True(job.GetAccounting().ActiveProcesses > 0);
                Assert.False(reads.IsCompleted); return code;
            });
            Assert.Equal(0, actual);
            Assert.Throws<IOException>(() => job.UseExitedRoot(root with { AttemptId = Guid.NewGuid() }, _ => true));
            Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0u, job.GetAccounting().ActiveProcesses);
            Assert.Throws<IOException>(() => job.UseExitedRoot(root, _ => true));
            var text = await reads.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Contains("root-tail", text[0]);
        }
        finally
        {
            Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
            output.CloseUnstartedEnds();
            await reads.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact(DisplayName = "A Still Running Original Root Cannot Issue An Exit Observation Or Run Its Persistence Callback")]
    public async Task RejectsRunningObservation()
    {
        var (directory, executable, marker) = Inputs();
        using var job = new OwnedWorkerJob(); using var output = new OwnedProcessOutput();
        using var stdout = new StreamReader(output.StandardOutput); using var stderr = new StreamReader(output.StandardError);
        var reads = Task.WhenAll(stdout.ReadToEndAsync(), stderr.ReadToEndAsync());
        try
        {
            var root = job.Start(executable, ["process-output", "wait", marker], directory,
                Guid.NewGuid(), Guid.NewGuid(), _ => { }, output);
            var timer = Stopwatch.StartNew();
            while (!File.Exists(marker + ".output-ready") && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
            Assert.True(File.Exists(marker + ".output-ready"));
            var invoked = false;
            var error = Assert.Throws<IOException>(() => job.UseExitedRoot(root, _ => { invoked = true; return true; }));
            Assert.Equal("worker_root_still_running", error.Message); Assert.False(invoked);
            Assert.Contains(root.ProcessId, job.ActiveProcessIds());
            Assert.Null(await job.AwaitRootAsync(root, TimeSpan.FromMilliseconds(50)));
        }
        finally
        {
            Assert.True(await job.StopAsync(TimeSpan.FromSeconds(10)));
            output.CloseUnstartedEnds();
            await reads.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static (string Directory, string Executable, string Marker) Inputs()
    {
        var temporary = Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT") ?? throw new IOException("fixture_temp_root_missing");
        var executable = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new IOException("fixture_image_missing");
        var directory = Path.Combine(temporary, "android-observed-root-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        return (directory, executable, Path.Combine(directory, "ready"));
    }
}
