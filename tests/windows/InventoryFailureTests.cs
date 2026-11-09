using System.Diagnostics;
using NexusPipeline.Platform.Processes;
using Xunit;

namespace NexusPipeline.WindowsTests;

public sealed class InventoryFailureTests
{
    [Fact(DisplayName = "An Inventory Failure Still Terminates The Actual Owned Job Without Issuing A Complete Stop Proof")]
    public async Task RejectsUnverifiedStops()
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!, "inventory-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var fixture = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        var marker = Path.Combine(directory, "ready");
        var boundary = new FailedInventory();
        using var job = new OwnedWorkerJob(boundary);
        using var output = new OwnedProcessOutput();
        var root = job.Start(fixture, ["process-output", "wait", marker], directory, Guid.NewGuid(), Guid.NewGuid(), _ => { }, output);
        var stdout = output.StandardOutput.CopyToAsync(Stream.Null);
        var stderr = output.StandardError.CopyToAsync(Stream.Null);
        using var process = Process.GetProcessById(root.ProcessId);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(marker + ".output-ready")) await Task.Delay(10, deadline.Token);
        Assert.False(process.HasExited);
        var error = await Assert.ThrowsAsync<IOException>(() => job.StopAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("controlled_inventory_failure", error.Message);
        Assert.Contains(root.ProcessId, boundary.ActualInventory!);
        await process.WaitForExitAsync(deadline.Token);
        await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token);
        Assert.True(await job.StopAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class FailedInventory : IJobStopObserver
    {
        internal int[]? ActualInventory { get; private set; }
        public void InventoryCaptured(IReadOnlyList<int> processIds)
        {
            if (ActualInventory is not null) return;
            ActualInventory = processIds.ToArray();
            throw new IOException("controlled_inventory_failure");
        }
    }
}
