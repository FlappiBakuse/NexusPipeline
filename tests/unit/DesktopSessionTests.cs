using NexusPipeline.Core.Desktop;
using Xunit;

namespace NexusPipeline.CoreTests;

public sealed class DesktopSessionTests
{
    [Theory(DisplayName = "Native Action Is Approved Once And A Repeated Request Cannot Replay It")]
    [InlineData(DesktopAction.SelectFile, null)]
    [InlineData(DesktopAction.SelectFolder, null)]
    [InlineData(DesktopAction.OpenExternal, "https://example.invalid/help")]
    public void PreventsNativeReplay(DesktopAction action, string? url)
    {
        var session = new DesktopSession(Guid.NewGuid(), Guid.NewGuid());
        var request = new DesktopActionRequest(session.Identity, Guid.NewGuid(), action, url);
        Assert.True(session.Authorize(request).IsApproved);
        Assert.Equal("desktop_request_already_seen", session.Authorize(request).ReasonCode);
    }

    [Fact(DisplayName = "A New Host Session Rejects Every Identifier From The Previous Generation")]
    public void RevokesPreviousSessions()
    {
        var instance = Guid.NewGuid();
        var client = Guid.NewGuid();
        var first = new DesktopSession(instance, client);
        var next = new DesktopSession(instance, client);
        var request = new DesktopActionRequest(first.Identity, Guid.NewGuid(), DesktopAction.SelectFile, null);
        Assert.False(next.Authorize(request).IsApproved);
        Assert.True(next.Authorize(request with { Identity = next.Identity }).IsApproved);
        Assert.False(next.Authorize(request with { Identity = next.Identity with { ClientId = Guid.NewGuid() } }).IsApproved);
        Assert.False(next.Authorize(request with { Identity = next.Identity with { InstanceId = Guid.NewGuid() } }).IsApproved);
        next.End();
        Assert.Equal("desktop_session_ended", next.Authorize(request with { Identity = next.Identity }).ReasonCode);
    }

    [Theory(DisplayName = "Invalid Native Arguments Do Not Consume A Request Identity")]
    [InlineData(DesktopAction.OpenExternal, "http://example.invalid/")]
    [InlineData(DesktopAction.OpenExternal, "file:///D:/private")]
    [InlineData(DesktopAction.OpenExternal, "https://user:secret@example.invalid/")]
    [InlineData(DesktopAction.OpenExternal, null)]
    [InlineData(DesktopAction.SelectFile, "https://example.invalid/")]
    public void RejectsInvalidArguments(DesktopAction action, string? url)
    {
        var session = new DesktopSession(Guid.NewGuid(), Guid.NewGuid());
        var request = new DesktopActionRequest(session.Identity, Guid.NewGuid(), action, url);
        Assert.False(session.Authorize(request).IsApproved);
        Assert.True(session.Authorize(request with { Action = DesktopAction.SelectFolder, Url = null }).IsApproved);
    }

    [Fact(DisplayName = "Reaching The Bounded Session Limit Never Makes Old Requests Replayable")]
    public void BoundsSessionReplay()
    {
        var session = new DesktopSession(Guid.NewGuid(), Guid.NewGuid());
        var original = new DesktopActionRequest(session.Identity, Guid.NewGuid(), DesktopAction.SelectFile, null);
        Assert.True(session.Authorize(original).IsApproved);
        for (var index = 1; index < DesktopSession.MaximumRequests; index++)
            Assert.True(session.Authorize(original with { CorrelationId = Guid.NewGuid() }).IsApproved);
        Assert.Equal("desktop_session_request_limit", session.Authorize(original with { CorrelationId = Guid.NewGuid() }).ReasonCode);
        Assert.Equal("desktop_request_already_seen", session.Authorize(original).ReasonCode);
    }
}
