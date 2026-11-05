using NexusPipeline.Core.Desktop;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class NativeClientTests
{
    [Fact(DisplayName = "Only The Original Native Session Can Retire A Bound Frontend Client Without Affecting Another Client")]
    public void EndsExactClient()
    {
        var instance = Guid.NewGuid(); using var sessions = new NativeClientSessions(instance);
        using var frontend = new CancellationTokenSource(); using var native = new CancellationTokenSource();
        var desktop = new DesktopSession(instance, Guid.NewGuid()); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        sessions.Bind(first, desktop.Identity, () => { }, frontend.Token, () => { }, native.Token);
        sessions.Bind(second, desktop.Identity, () => { }, frontend.Token, () => { }, native.Token);
        var access = sessions.Require(first);
        Assert.Throws<UnauthorizedAccessException>(() => sessions.EndClient(first, desktop.Identity with { SessionId = Guid.NewGuid() }));
        access.RequireCurrent(); sessions.EndClient(first, desktop.Identity); sessions.EndClient(first, desktop.Identity);
        Assert.True(access.Lifetime.IsCancellationRequested); sessions.Require(second).RequireCurrent();
    }
    [Theory(DisplayName = "A Native Binding Belongs To One Actual Frontend Grant And Ends With Either Original Lifetime")]
    [InlineData(false)]
    [InlineData(true)]
    public void BindsActualClient(bool nativeEnds)
    {
        var instance = Guid.NewGuid(); using var sessions = new NativeClientSessions(instance);
        using var frontend = new CancellationTokenSource(); using var native = new CancellationTokenSource();
        var desktop = new DesktopSession(instance, Guid.NewGuid()); var client = Guid.NewGuid(); var reads = 0;
        sessions.Bind(client, desktop.Identity, () => reads++, frontend.Token, () => desktop.RequireCurrent(desktop.Identity), native.Token);
        var access = sessions.Require(client); Assert.Equal(client, access.ClientId); Assert.Equal(desktop.Identity, access.Desktop);
        Assert.Throws<UnauthorizedAccessException>(() => sessions.Require(Guid.NewGuid()));
        sessions.Bind(client, desktop.Identity, () => reads++, frontend.Token, () => desktop.RequireCurrent(desktop.Identity), native.Token);
        Assert.Throws<UnauthorizedAccessException>(() => sessions.Bind(client, new(instance, Guid.NewGuid(), Guid.NewGuid()),
            () => { }, frontend.Token, () => { }, native.Token));
        if (nativeEnds) native.Cancel(); else frontend.Cancel();
        Assert.True(access.Lifetime.IsCancellationRequested);
        Assert.Throws<UnauthorizedAccessException>(() => access.RequireCurrent());
        Assert.Throws<UnauthorizedAccessException>(() => sessions.Require(client)); Assert.True(reads >= 3);
    }

    [Theory(DisplayName = "Captured Native Access Rechecks Original Frontend Authority And The Live Original Desktop Session")]
    [InlineData(false)]
    [InlineData(true)]
    public void RechecksOriginalAuthority(bool nativeRevoked)
    {
        var instance = Guid.NewGuid(); using var sessions = new NativeClientSessions(instance);
        using var frontend = new CancellationTokenSource(); using var native = new CancellationTokenSource();
        var desktop = new DesktopSession(instance, Guid.NewGuid()); var client = Guid.NewGuid(); var allowed = true;
        sessions.Bind(client, desktop.Identity, () => { if (!allowed) throw new UnauthorizedAccessException(); }, frontend.Token,
            () => desktop.RequireCurrent(desktop.Identity), native.Token);
        var access = sessions.Require(client);
        if (nativeRevoked) desktop.End(); else allowed = false;
        Assert.Throws<UnauthorizedAccessException>(() => access.RequireCurrent());
        Assert.True(access.Lifetime.IsCancellationRequested); Assert.Throws<UnauthorizedAccessException>(() => sessions.Require(client));
    }

    [Fact(DisplayName = "Ending One Native Session Retires Exactly Its Frontend Grants And Unbound Lifetimes Cannot Create Authority")]
    public void EndsExactSession()
    {
        var instance = Guid.NewGuid(); using var sessions = new NativeClientSessions(instance);
        using var frontend = new CancellationTokenSource(); using var native = new CancellationTokenSource();
        var first = new DesktopSession(instance, Guid.NewGuid()); var second = new DesktopSession(instance, Guid.NewGuid());
        var one = Guid.NewGuid(); var two = Guid.NewGuid();
        sessions.Bind(one, first.Identity, () => { }, frontend.Token, () => first.RequireCurrent(first.Identity), native.Token);
        sessions.Bind(two, second.Identity, () => { }, frontend.Token, () => second.RequireCurrent(second.Identity), native.Token);
        Assert.Throws<UnauthorizedAccessException>(() => sessions.Bind(Guid.NewGuid(), second.Identity, () => { }, default, () => { }, native.Token));
        var access = sessions.Require(one); sessions.EndSession(first.Identity.SessionId);
        Assert.True(access.Lifetime.IsCancellationRequested); Assert.Throws<UnauthorizedAccessException>(() => sessions.Require(one));
        sessions.Require(two).RequireCurrent(); sessions.Dispose(); Assert.Throws<UnauthorizedAccessException>(() => sessions.Require(two));
    }
}
