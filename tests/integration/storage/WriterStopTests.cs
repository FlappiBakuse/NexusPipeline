using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Execution;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class WriterStopTests
{
    [Fact(DisplayName = "An Observed Real Child Exiting After The Stop Inventory Cannot Invalidate Its Original Native Handle Proof")]
    public async Task RetainsObservedHandles()
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "stop-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "root.ready");
        var boundary = new ChildExitBoundary();
        using var job = new OwnedWorkerJob(boundary);
        using var output = new OwnedProcessOutput();
        var observed = new ConcurrentDictionary<ProcessIdentity, byte>();
        using var observer = job.ObserveProcessIdentities(identity => observed.TryAdd(identity, 0));
        var root = job.Start(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!,
            ["process-output", "child-wait", marker], directory, Guid.NewGuid(), Guid.NewGuid(), _ => { }, output);
        var stdout = Drain(output.StandardOutput); var stderr = Drain(output.StandardError);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(marker + ".child.output-ready")) await Task.Delay(10, deadline.Token);
        Assert.True(observer.Drain(TimeSpan.FromSeconds(5))); Assert.Empty(observer.Failures);
        var childPid = int.Parse(File.ReadAllText(marker + ".child"));
        var child = Assert.Single(observed.Keys, value => value.ProcessId == childPid);
        Assert.NotEqual(root.ProcessId, child.ProcessId);
        boundary.Child = child;
        var stopped = await job.StopAsync(TimeSpan.FromSeconds(10));
        Assert.True(observer.Drain(TimeSpan.FromSeconds(5)));
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        { root, child, stopped, job.LastStopObservation, accounting = job.GetAccounting(), observed = observed.Keys, observer.Failures }));
        Assert.True(stopped, JsonSerializer.Serialize(job.LastStopObservation));
        Assert.True(boundary.Reached); Assert.Empty(observer.Failures);
        Assert.Equal((uint)observed.Count, job.GetAccounting().TotalProcesses);
        Assert.All(observed.Keys, value => Assert.Equal(WriterState.Stopped, WriterStopEvidence.ObserveProcess(value)));
        await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class ChildExitBoundary : IJobStopObserver
    {
        internal ProcessIdentity Child { get; set; } = null!;
        internal bool Reached { get; private set; }

        public void InventoryCaptured(IReadOnlyList<int> processIds)
        {
            Assert.Contains(Child.ProcessId, processIds); Reached = true;
            using (var process = JobNative.OpenProcess(0x101001, false, Child.ProcessId))
            {
                Assert.False(process.IsInvalid); Assert.Equal(Child, ProcessIdentityReader.Read(process, Child.ProcessId));
                Assert.True(JobNative.TerminateProcess(process, 1));
                Assert.Equal(0u, JobNative.WaitForSingleObject(process, 10000));
            }
            Thread.Sleep(100);
        }
    }

    private static async Task Drain(Stream stream)
    {
        var bytes = new byte[4096];
        while (await stream.ReadAsync(bytes) != 0) { }
    }
}
