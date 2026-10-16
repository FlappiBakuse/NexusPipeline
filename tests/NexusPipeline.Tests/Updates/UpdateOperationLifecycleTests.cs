using System.Net;
using NexusPipeline.Modules.Updates;
using Xunit;
namespace NexusPipeline.Tests.Updates;
public sealed class UpdateOperationLifecycleTests
{
    [Fact]
    public async Task CancellationRetainsTheExactCompletionAndRejectsNewWorkUntilFinallyEnds()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string root = Path.Combine(Path.GetTempPath(), "nxp-operation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var handler = new Handler(async token => {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new HttpResponseMessage(HttpStatusCode.OK); }
            finally { draining.TrySetResult(); await release.Task; }
        });
        var engine = new UpdateEngine(() => new("https://fixture.example/releases", "prerelease"), "0.17.4", root,
            createClient: (_, _, _) => new HttpClient(handler, false));
        try
        {
            var check = engine.CheckAsync("test");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var completion = engine.WaitForCurrentOperationAsync();
            var close = engine.CancelAndWaitAsync();
            await draining.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(UpdateState.Cancelling, engine.State);
            Assert.Same(completion, engine.WaitForCurrentOperationAsync());
            Assert.False(completion.IsCompleted); Assert.False(close.IsCompleted);
            Assert.Equal(UpdateState.Cancelling, (await engine.CheckAsync("test")).State);
            Assert.False(engine.StartDownload("test").Succeeded);
            Assert.False(engine.RequestApply(false, "test").Succeeded);
            release.TrySetResult();
            await Task.WhenAll(check, completion, close).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(UpdateState.Idle, engine.State);
            Assert.True(engine.WaitForCurrentOperationAsync().IsCompleted);
        }
        finally { release.TrySetResult(); await engine.CancelAndWaitAsync(); Directory.Delete(root, true); }
    }
    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(token);
    }
}
