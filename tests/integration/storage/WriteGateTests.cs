using System.Diagnostics;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

public sealed class WriteGateTests
{
    [Fact(DisplayName = "Full Write Capacity Rejects Immediately While The Admitted Writer Remains Serialized")]
    public async Task BoundsWriteCapacity()
    {
        var gate = new DatabaseWriteGate(2, TimeSpan.FromSeconds(3));
        using var first = gate.Enter();
        using var release = new ManualResetEventSlim();
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = Task.Run(() =>
        {
            using var second = gate.Enter();
            acquired.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        });
        try
        {
            await WaitPendingAsync(gate, 2);
            Assert.False(acquired.Task.IsCompleted);
            var elapsed = Stopwatch.StartNew();
            Assert.Equal("database_writer_capacity_exceeded", Assert.Throws<IOException>(() => gate.Enter()).Message);
            Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(500));
            first.Dispose();
            await acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, gate.PendingCount);
        }
        finally
        {
            first.Dispose();
            release.Set();
            await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0, gate.PendingCount);
    }

    [Fact(DisplayName = "A Queued Timeout Returns Capacity And Does Not Consume The Next Writer")]
    public void ReleasesExpiredSlots()
    {
        var gate = new DatabaseWriteGate(2, TimeSpan.FromMilliseconds(100));
        using var first = gate.Enter();
        Assert.Equal("database_writer_busy", Assert.Throws<IOException>(() => gate.Enter()).Message);
        Assert.Equal(1, gate.PendingCount);
        first.Dispose();
        using var next = gate.Enter();
        Assert.Equal(1, gate.PendingCount);
    }

    [Fact(DisplayName = "A Cancelled Queued Write Returns Capacity Without Releasing The Active Writer")]
    public async Task ReleasesCancelledSlots()
    {
        var gate = new DatabaseWriteGate(2, TimeSpan.FromSeconds(3));
        using var first = gate.Enter();
        using var cancellation = new CancellationTokenSource();
        var waiting = Task.Run(() => gate.Enter(cancellation.Token));
        await WaitPendingAsync(gate, 2);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var unexpected = await waiting; });
        Assert.Equal(1, gate.PendingCount);
        first.Dispose();
        using var next = gate.Enter();
        Assert.Equal(1, gate.PendingCount);
    }

    private static async Task WaitPendingAsync(DatabaseWriteGate gate, int expected)
    {
        var elapsed = Stopwatch.StartNew();
        while (gate.PendingCount != expected && elapsed.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(5);
        Assert.Equal(expected, gate.PendingCount);
    }
}
