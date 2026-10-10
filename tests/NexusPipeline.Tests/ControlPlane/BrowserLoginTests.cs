using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.BrowserLogin;
using NexusPipeline.Modules.ClientSessions;
using NexusPipeline.Plugin.Abstractions;
using Xunit;

namespace NexusPipeline.Tests.ControlPlane;

public sealed class BrowserLoginTests
{
    private static PluginBrowserLoginFlow Flow(bool ready = true) => new("example", "Example", "1", "https://example.com/login",
        ["https://example.com"], [], [new(".example.com", "/", "session")], [], ready);
    private static PluginBrowserCapture Capture() => new([new(".example.com", "/", "session", "private-cookie")], [], DateTimeOffset.UtcNow);
    private static JsonObject Json(object value) => JsonSerializer.SerializeToNode(value, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
    private static string Begin(BrowserLoginService service, PluginClientSessionContext client, string editor = "editor") =>
        Json(service.Begin(client, "owner:example", editor, 0, new()))["operationId"]!.GetValue<string>();
    private static ValueTask<PluginBrowserLoginResult> Complete(PluginBrowserCapture capture, PluginBrowserInvocation invocation, CancellationToken token) =>
        ValueTask.FromResult(new PluginBrowserLoginResult(true, "candidate", true, "browser", "12****89"));

    [Fact]
    public void ClientSessionsNeverGrantNativeToHttpAndRevokeOnAuthorityOrDesktopExit()
    {
        using var service = new ClientSessionService();service.SetAuthority("first");
        var web = service.CreateWeb();var desktop = service.CreateDesktop("verified-owner");
        Assert.False(web.Context.NativeBrowserAvailable);Assert.Equal("web", web.Context.ClientKind);
        Assert.True(desktop.Context.NativeBrowserAvailable);Assert.Equal(desktop.Token, service.CreateDesktop("verified-owner").Token);
        Assert.NotEqual(web.Context.ClientSessionId, service.CreateWeb().Context.ClientSessionId);
        Assert.Null(service.Resolve(desktop.Token, false));Assert.Equal(desktop.Context, service.Resolve(desktop.Token, true));
        service.RevokeDesktop("verified-owner");Assert.True(desktop.Context.Lifetime.IsCancellationRequested);
        Assert.Null(service.Resolve(desktop.Token, true));Assert.NotNull(service.Resolve(web.Token, false));
        service.SetAuthority("second");Assert.True(web.Context.Lifetime.IsCancellationRequested);Assert.Null(service.Resolve(web.Token, true));
        using var restarted = new ClientSessionService();Assert.NotEqual(web.Context.HostSessionId, restarted.CreateWeb().Context.HostSessionId);
        string authority="before";using var dynamic = new ClientSessionService(()=>authority);
        var issued=dynamic.CreateDesktop("same-owner");authority="after";
        var replacement=dynamic.CreateDesktop("same-owner");
        Assert.True(issued.Context.Lifetime.IsCancellationRequested);Assert.NotEqual(issued.Token,replacement.Token);
        Assert.Null(dynamic.Resolve(issued.Token,true));Assert.NotNull(dynamic.Resolve(replacement.Token,true));
    }

    [Fact]
    public void BrowserAdmissionRequiresTrustedClientReadyFlowAndPluginBinding()
    {
        using var clients = new ClientSessionService();using var service = new BrowserLoginService();
        var desktop = clients.CreateDesktop("verified-owner").Context;
        using var registration = service.Register("owner", Flow(), Complete, (_, _, _) => ValueTask.CompletedTask,
            invocation => { if (invocation.EditorSessionId != "editor") throw new InvalidOperationException("binding_invalid"); });
        Assert.Throws<InvalidOperationException>(() => Begin(service, clients.CreateWeb().Context));
        Assert.Throws<InvalidOperationException>(() => Begin(service, desktop, "wrong-editor"));
        string id = Begin(service, desktop);Assert.Equal(id, Begin(service, desktop));
        Assert.Throws<InvalidOperationException>(() => service.Begin(desktop, "owner:example", "editor", 1, new()));
        Assert.Throws<InvalidOperationException>(() => service.Status(clients.CreateDesktop("other-owner").Context, id));
        using var other = new BrowserLoginService();using var unavailable = other.Register("owner", Flow(false), Complete, (_, _, _) => ValueTask.CompletedTask, _ => { });
        Assert.Equal("platform_not_ready", Assert.Throws<InvalidOperationException>(() => Begin(other, desktop)).Message);
        Assert.Throws<InvalidDataException>(() => other.Register("bad", Flow() with { StartUri = "http://example.com/" }, Complete, (_, _, _) => ValueTask.CompletedTask, _ => { }));
    }

    [Fact]
    public async Task BrowserSuccessPublishesOnlyAfterCleanupAndActivation()
    {
        using var clients = new ClientSessionService();using var service = new BrowserLoginService();var client = clients.CreateDesktop("verified").Context;
        var notifications = new List<PluginBrowserLoginTerminal>();
        using var registration = service.Register("owner", Flow(), Complete, (_, terminal, token) =>
        { Assert.False(token.IsCancellationRequested);notifications.Add(terminal);return ValueTask.CompletedTask; }, _ => { });
        string id = Begin(service, client);
        var provisional = await service.CompleteAsync(client, id, Capture());Assert.True(provisional.Success);Assert.Null(provisional.CandidateId);
        Assert.Null(Json(service.Status(client, id))["result"]);Assert.Empty(notifications);
        await service.CleanedAsync(client, id, true);
        var status = Json(service.Status(client, id));Assert.Equal("completed", status["state"]!.GetValue<string>());
        Assert.Equal("candidate", status["result"]!["candidateId"]!.GetValue<string>());Assert.DoesNotContain("private-cookie", status.ToJsonString());
        await service.CleanedAsync(client, id, true);await service.CancelAsync(client, id);
        Assert.Equal([PluginBrowserLoginTerminal.Completed], notifications);
    }

    [Fact]
    public async Task CancelDuringActivationCompensatesBeforeAnyPublicSuccess()
    {
        using var clients = new ClientSessionService();using var service = new BrowserLoginService();var client = clients.CreateDesktop("verified").Context;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new List<PluginBrowserLoginTerminal>();
        using var registration = service.Register("owner", Flow(), Complete, async (_, terminal, token) =>
        { notifications.Add(terminal);Assert.False(token.IsCancellationRequested);if (terminal == PluginBrowserLoginTerminal.Completed) { entered.SetResult();await release.Task; } }, _ => { });
        string id = Begin(service, client);await service.CompleteAsync(client, id, Capture());
        var cleanup = service.CleanedAsync(client, id, true);await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var cancellation = service.CancelAsync(client, id);Assert.Null(Json(service.Status(client, id))["result"]);
        release.SetResult();await Task.WhenAll(cleanup, cancellation);
        Assert.Equal("cancelled", Json(service.Status(client, id))["state"]!.GetValue<string>());
        Assert.False(Json(service.Status(client, id))["result"]!["success"]!.GetValue<bool>());
        Assert.Equal([PluginBrowserLoginTerminal.Completed, PluginBrowserLoginTerminal.Cancelled], notifications);
    }

    [Fact]
    public async Task CaptureScopeFailuresRetryButCleanupOrActivationFailuresRevokeCandidates()
    {
        using var clients = new ClientSessionService();using var service = new BrowserLoginService();var client = clients.CreateDesktop("verified").Context;
        int captures = 0;var notifications = new List<PluginBrowserLoginTerminal>();
        using var registration = service.Register("owner", Flow(), (capture, invocation, token) => { captures++;return Complete(capture, invocation, token); },
            (_, terminal, _) => { notifications.Add(terminal);if (terminal == PluginBrowserLoginTerminal.Completed) throw new InvalidOperationException();return ValueTask.CompletedTask; }, _ => { });
        string id = Begin(service, client);
        Assert.False((await service.CompleteAsync(client, id, Capture() with { Cookies = [new(".other.com", "/", "session", "unapproved")] })).Success);
        Assert.Equal(0, captures);Assert.Equal("open", Json(service.Status(client, id))["state"]!.GetValue<string>());
        Assert.True((await service.CompleteAsync(client, id, Capture())).Success);await service.CleanedAsync(client, id, true);
        Assert.Equal("browser_activation_failed", Json(service.Status(client, id))["result"]!["error"]!.GetValue<string>());
        Assert.Contains(PluginBrowserLoginTerminal.Cancelled, notifications);
        string second = Begin(service, client);await service.CompleteAsync(client, second, Capture());await service.CleanedAsync(client, second, false);
        Assert.Equal("browser_cleanup_failed", Json(service.Status(client, second))["result"]!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task SessionRevocationAndEditorCancellationTerminatePendingValidation()
    {
        using var clients = new ClientSessionService();using var service = new BrowserLoginService();var client = clients.CreateDesktop("verified").Context;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int revoked = 0;
        using var registration = service.Register("owner", Flow(), async (_, _, token) => { entered.SetResult();await Task.Delay(Timeout.Infinite, token);return new(false); },
            (_, terminal, token) => { Assert.False(token.IsCancellationRequested);Assert.Equal(PluginBrowserLoginTerminal.Cancelled, terminal);revoked++;return ValueTask.CompletedTask; }, _ => { });
        string id = Begin(service, client);var validation = service.CompleteAsync(client, id, Capture());await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await service.CancelEditorAsync("owner", client, "editor");Assert.False((await validation).Success);Assert.Equal(1, revoked);
        clients.RevokeDesktop("verified");Assert.Throws<InvalidOperationException>(() => service.Status(client, id));
    }
}
