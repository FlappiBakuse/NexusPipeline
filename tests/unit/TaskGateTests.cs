using NexusPipeline.Core.Scheduling;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class TaskGateTests
{
    [Fact(DisplayName = "Only One Actual Run Can Own A Business Task While Admission Tracking Still Drains With That Task")]
    public async Task OwnsExclusiveTask()
    {
        var gate = new WorkTaskGate(); var taskId = Guid.NewGuid(); var runId = Guid.NewGuid();
        using var admission = gate.Enter(taskId);
        using var executing = gate.EnterExclusive(taskId, runId);
        gate.RequireExclusive(taskId, runId);
        Assert.Throws<UnauthorizedAccessException>(() => gate.RequireExclusive(taskId, Guid.NewGuid()));
        Assert.Equal("work_task_running", Assert.Throws<InvalidOperationException>(() => gate.EnterExclusive(taskId, Guid.NewGuid())).Message);
        var draining = gate.QuiesceAsync([taskId], default).AsTask();
        Assert.Throws<UnauthorizedAccessException>(() => gate.RequireExclusive(taskId, runId));
        executing.Dispose(); Assert.False(draining.IsCompleted);
        admission.Dispose(); using var held = await draining;
        Assert.Throws<InvalidOperationException>(() => gate.EnterExclusive(taskId, Guid.NewGuid()));
    }

    [Theory(DisplayName = "Task Quiescence Cancels Related Work And Retains Entry Exclusion Until The Actual Callback Returns")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainsActualWork(bool cancelWaiter)
    {
        var gate = new WorkTaskGate(); var taskId = Guid.NewGuid(); var related = Guid.NewGuid();
        using var work = gate.Enter(taskId); using var sibling = gate.Enter(related);
        using var unrelated = gate.Enter(Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        var pending = gate.QuiesceAsync([taskId, related], cancellation.Token).AsTask();
        Assert.True(work.Cancellation.IsCancellationRequested); Assert.True(sibling.Cancellation.IsCancellationRequested);
        Assert.False(unrelated.Cancellation.IsCancellationRequested); Assert.False(pending.IsCompleted);
        Assert.Equal("work_task_quiescing", Assert.Throws<InvalidOperationException>(() => gate.Enter(taskId)).Message);
        if (cancelWaiter)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Throws<InvalidOperationException>(() => gate.Enter(taskId));
        }
        work.Dispose();
        Assert.Throws<InvalidOperationException>(() => gate.Enter(related));
        sibling.Dispose();
        if (cancelWaiter) return;
        using (await pending.WaitAsync(TimeSpan.FromSeconds(5))) Assert.Throws<InvalidOperationException>(() => gate.Enter(taskId));
        using var resumed = gate.Enter(taskId);
        Assert.False(resumed.Cancellation.IsCancellationRequested);
    }

    [Fact(DisplayName = "Overlapping Task Holds Release Independently And Reject Identity Substitution")]
    public async Task KeepsIndependentHolds()
    {
        var gate = new WorkTaskGate(); var taskId = Guid.NewGuid();
        var first = await gate.QuiesceAsync([taskId], default);
        var second = await gate.QuiesceAsync([taskId], default);
        first.Dispose(); first.Dispose();
        Assert.Throws<InvalidOperationException>(() => gate.Enter(taskId));
        second.Dispose(); using var accepted = gate.Enter(taskId);
        await Assert.ThrowsAsync<ArgumentException>(() => gate.QuiesceAsync([taskId, taskId], default).AsTask());
        Assert.False(accepted.Cancellation.IsCancellationRequested);
    }

    [Fact(DisplayName = "Cancellation Callback Failure Cannot Grant Quiescence Before Outstanding Work Actually Releases")]
    public async Task PreservesFailedCancellation()
    {
        var gate = new WorkTaskGate(); var taskId = Guid.NewGuid();
        using var work = gate.Enter(taskId);
        using var callback = work.Cancellation.Register(() => throw new IOException("fixture_callback_failure"));
        var pending = gate.QuiesceAsync([taskId], default).AsTask();
        Assert.False(pending.IsCompleted); Assert.Throws<InvalidOperationException>(() => gate.Enter(taskId));
        work.Dispose();
        await Assert.ThrowsAsync<AggregateException>(() => pending);
        using var resumed = gate.Enter(taskId);
    }

    [Fact(DisplayName = "Task Quiescence Capacity Rejection Does Not Partially Block An Unselected Task")]
    public async Task BoundsTaskHolds()
    {
        var gate = new WorkTaskGate(); var taskId = Guid.NewGuid(); var held = new List<IDisposable>();
        try
        {
            for (var index = 0; index < 32; index++) held.Add(await gate.QuiesceAsync([taskId], default));
            var other = Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidOperationException>(() => gate.QuiesceAsync([other], default).AsTask());
            using var unaffected = gate.Enter(other);
            Assert.False(unaffected.Cancellation.IsCancellationRequested);
        }
        finally { foreach (var hold in held) hold.Dispose(); }
        using var restored = gate.Enter(taskId);
    }
}
