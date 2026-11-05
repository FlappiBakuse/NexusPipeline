using NexusPipeline.Platform.Extensions;
using Xunit;

namespace NexusPipeline.ContractTests;

public sealed class ExtensionScopeTests
{
    [Fact(DisplayName = "Disposal Cancels Before Reverse Cleanup And Shares Its Completion")]
    public async Task SharesOrderedDisposal()
    {
        var scope = new ExtensionScope("fixture");
        var events = new List<string>();
        scope.Cancellation.Register(() => events.Add("cancel"));
        await scope.OwnAsync(new Resource(() => events.Add("first")));
        await scope.OwnAsync(new Resource(() => events.Add("second")));
        await Task.WhenAll(scope.DisposeAsync().AsTask(), scope.DisposeAsync().AsTask());
        Assert.Equal(["cancel", "second", "first"], events);
        Assert.True(scope.Cancellation.IsCancellationRequested);
    }

    [Fact(DisplayName = "Late Resources Are Disposed And Cleanup Failures Do Not Skip Earlier Resources")]
    public async Task PreservesCleanupOrder()
    {
        var scope = new ExtensionScope("fixture");
        var first = false;
        await scope.OwnAsync(new Resource(() => first = true));
        await scope.OwnAsync(new Resource(() => throw new IOException("fixture failure")));
        await Assert.ThrowsAsync<AggregateException>(() => scope.DisposeAsync().AsTask());
        Assert.True(first);
        var late = false;
        await scope.OwnAsync(new Resource(() => late = true));
        Assert.True(late);
        Assert.True(scope.Cancellation.IsCancellationRequested);
    }

    private sealed class Resource(Action cleanup) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            cleanup();
            return ValueTask.CompletedTask;
        }
    }
}
