using NexusPipeline.Core.Desktop;
using NexusPipeline.Core.Secrets;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class BrowserLoginTests
{
    [Fact(DisplayName = "Native Capture Copies Confirmed Secret Material Only After Its Exact Peer Cleanup Acknowledgment")]
    public async Task CompletesOwnedCapture()
    {
        using var fixture = new Fixture(); await using var service = new BrowserLoginSessions();
        Assert.False(service.CanCapture(fixture.Access)); Assert.Empty(service.Poll(fixture.Native));
        var pending = service.CaptureAsync(fixture.Access, "fixture", Guid.NewGuid(), Guid.NewGuid(), Policy(), () => { }, fixture.Callback.Token, default).AsTask();
        var offer = Assert.Single(service.Poll(fixture.Native));
        using var value = new SecretValue("synthetic");
        var values = new Dictionary<string, SecretValue> { ["token"] = value };
        Assert.Throws<UnauthorizedAccessException>(() => service.Complete(fixture.Native, offer.CaptureId, Guid.NewGuid(), "completed", values));
        Assert.Throws<UnauthorizedAccessException>(() => service.Complete(fixture.Native with { ClientId = Guid.NewGuid() }, offer.CaptureId, offer.Nonce, "completed", values));
        Assert.False(pending.IsCompleted);
        service.Complete(fixture.Native, offer.CaptureId, offer.Nonce, "completed", values);
        using var material = await pending; value.Dispose();
        var read = false; material.Values["token"].Use(text => read = text.SequenceEqual("synthetic")); Assert.True(read);
        material.Dispose(); Assert.Throws<ObjectDisposedException>(() => material.Values["token"].Use(_ => { }));
        Assert.Empty(service.Poll(fixture.Native));
    }

    [Theory(DisplayName = "Caller Retirement Waits For Native Cleanup And Late Capture Cannot Publish Its Secret")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainsCanceledCapture(bool clientEnds)
    {
        using var fixture = new Fixture(); await using var service = new BrowserLoginSessions(); service.Poll(fixture.Native);
        var pending = service.CaptureAsync(fixture.Access, "fixture", Guid.NewGuid(), Guid.NewGuid(), Policy(), () => { }, fixture.Callback.Token, default).AsTask();
        var offer = Assert.Single(service.Poll(fixture.Native));
        if (clientEnds) fixture.Frontend.Cancel(); else fixture.Callback.Cancel();
        Assert.False(pending.IsCompleted); Assert.True(Assert.Single(service.Poll(fixture.Native)).IsCanceled);
        using var secret = new SecretValue("synthetic-late");
        service.Complete(fixture.Native, offer.CaptureId, offer.Nonce, "completed", new Dictionary<string, SecretValue> { ["token"] = secret });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact(DisplayName = "Disposing Native Binding Is Not Physical Exit Proof While Actual Exit Releases A Canceled Capture")]
    public async Task RequiresNativeExit()
    {
        using var fixture = new Fixture(); await using var service = new BrowserLoginSessions(); service.Poll(fixture.Native);
        var pending = service.CaptureAsync(fixture.Access, "fixture", Guid.NewGuid(), Guid.NewGuid(), Policy(), () => { }, fixture.Callback.Token, default).AsTask();
        service.Poll(fixture.Native); fixture.NativeLife.Cancel(); Assert.False(pending.IsCompleted);
        fixture.Exited.SetResult();
        Assert.Equal("browser_login_native_exited", (await Assert.ThrowsAsync<IOException>(() => pending)).Message);
    }

    [Fact(DisplayName = "Only One Pending Capture Per Actual Client Is Allowed And An Unoffered Cancel Needs No Window Cleanup")]
    public async Task BoundsClientCapture()
    {
        using var fixture = new Fixture(); await using var service = new BrowserLoginSessions();
        await Assert.ThrowsAsync<IOException>(() => service.CaptureAsync(fixture.Access, "fixture", Guid.NewGuid(), Guid.NewGuid(), Policy(), () => { }, fixture.Callback.Token, default).AsTask());
        service.Poll(fixture.Native);
        var pending = service.CaptureAsync(fixture.Access, "fixture", Guid.NewGuid(), Guid.NewGuid(), Policy(), () => { }, fixture.Callback.Token, default).AsTask();
        await Assert.ThrowsAsync<IOException>(() => service.CaptureAsync(fixture.Access, "fixture", Guid.NewGuid(), Guid.NewGuid(), Policy(), () => { }, fixture.Callback.Token, default).AsTask());
        fixture.Callback.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(service.Poll(fixture.Native));
    }

    [Fact(DisplayName = "Malformed Captured Material Cannot Become A Successful Login Even With A Genuine Native Peer")]
    public async Task RejectsIncompleteCapture()
    {
        using var fixture = new Fixture(); await using var service = new BrowserLoginSessions(); service.Poll(fixture.Native);
        var pending = service.CaptureAsync(fixture.Access, "fixture", Guid.NewGuid(), Guid.NewGuid(), Policy(), () => { }, fixture.Callback.Token, default).AsTask();
        var offer = Assert.Single(service.Poll(fixture.Native)); using var secret = new SecretValue("contains space");
        service.Complete(fixture.Native, offer.CaptureId, offer.Nonce, "completed", new Dictionary<string, SecretValue> { ["token"] = secret });
        Assert.Equal("browser_login_capture_incomplete", (await Assert.ThrowsAsync<IOException>(() => pending)).Message);
    }

    [Fact(DisplayName = "A Cleanup Timeout Fails Stop Without Releasing The Pending Capture Or Accepting A Late Secret")]
    public async Task PreservesUnconfirmedCleanup()
    {
        using var fixture = new Fixture(); await using var service = new BrowserLoginSessions(); service.Poll(fixture.Native);
        var pending = service.CaptureAsync(fixture.Access, "fixture", Guid.NewGuid(), Guid.NewGuid(), Policy(), () => { }, fixture.Callback.Token, default).AsTask();
        var offer = Assert.Single(service.Poll(fixture.Native));
        Assert.Equal("browser_login_cleanup_unconfirmed", (await Assert.ThrowsAsync<IOException>(() => service.StopAsync(TimeSpan.FromMilliseconds(20)).AsTask())).Message);
        Assert.False(pending.IsCompleted); Assert.True(Assert.Single(service.Poll(fixture.Native)).IsCanceled);
        using var late = new SecretValue("synthetic");
        service.Complete(fixture.Native, offer.CaptureId, offer.Nonce, "completed", new Dictionary<string, SecretValue> { ["token"] = late });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static BrowserLoginPolicy Policy() => BrowserPolicyRules.Freeze(new("fixture", "https://login.example.test/", ["https://login.example.test"], [],
        [new("token", ".example.test", "/", "token")], [], [], [["token"]], [new("token", "token", [], 128)], []), ["https://login.example.test"]);

    private sealed class Fixture : IDisposable
    {
        internal CancellationTokenSource Frontend { get; } = new();
        internal CancellationTokenSource NativeLife { get; } = new();
        internal CancellationTokenSource Callback { get; } = new();
        internal TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal NativeClientSessions Sessions { get; }
        internal DesktopSessionIdentity Native { get; }
        internal NativeClientAccess Access { get; }
        internal Fixture()
        {
            var instance = Guid.NewGuid(); var client = Guid.NewGuid(); Native = new(instance, Guid.NewGuid(), Guid.NewGuid()); Sessions = new(instance);
            Sessions.Bind(client, Native, () => { }, Frontend.Token, () => { }, NativeLife.Token, Exited.Task); Access = Sessions.Require(client);
        }
        public void Dispose() { Sessions.Dispose(); Frontend.Dispose(); NativeLife.Dispose(); Callback.Dispose(); }
    }
}
