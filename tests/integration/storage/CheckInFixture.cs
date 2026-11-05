using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Desktop;
using NexusPipeline.Core.Notifications;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Notifications;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class CheckInFixture : IAsyncDisposable
{
    internal ExecutionAdmissionFixture Execution { get; } = new();
    internal ExtensionCatalogFixture Catalog { get; }
    internal PersistentScheduleService Schedules { get; }
    internal SecretFieldService Secrets { get; }
    internal InstallRuntimeFixture Installed { get; }
    private readonly HttpMessageHandler _http;
    internal CheckInHttp Http => (CheckInHttp)_http;
    internal InstalledFrontend Frontend { get; private set; } = null!;
    internal NotificationRuntime Notifications { get; }
    internal NativeClientSessions? Clients { get; }
    internal BrowserLoginSessions? Logins { get; }
    internal DesktopSessionIdentity? Native { get; }
    private readonly CancellationTokenSource _client = new();
    private readonly Guid _clientId = Guid.NewGuid();

    internal CheckInFixture(BuiltExtensionInputs inputs, int responseCode) : this(inputs, new CheckInHttp(responseCode)) { }

    internal CheckInFixture(BuiltExtensionInputs inputs, HttpMessageHandler http, TimeProvider? clock = null, bool native = false)
    {
        Catalog = Execution.CreateCatalog(); var source = Execution.Source;
        Schedules = new(source.Instance, source.Database, source.Lease, Execution.Resources, Execution.Lifecycle, Execution.Index, Execution.Service, clock);
        Secrets = new(source.Instance, source.Database, source.Lease, Execution.Resources, Execution.Index, new());
        Notifications = new(source.Instance, source.Database, new(), source.Lease, Execution.Resources, Execution.Index, Secrets);
        Notifications.InitializeStartup(); Notifications.BindSchedules(Schedules);
        if (native)
        {
            Clients = new(Catalog.Instance.InstanceId); Logins = new();
            Native = new(Catalog.Instance.InstanceId, Guid.NewGuid(), Guid.NewGuid());
            Clients.Bind(_clientId, Native, () => { }, _client.Token, () => { }, _client.Token); Logins.Poll(Native);
        }
        _http = http;
        Installed = new(catalog: Catalog, portDependencies: new(Schedules, Secrets,
            new SecretAccessPolicy(source.Instance, source.Database, source.Lease, Execution.Resources), null, null, null,
            () => new HttpClient(_http, disposeHandler: false), NativeClients: Clients, BrowserLogins: Logins), originalManifest: inputs.Manifest, originalManifestBytes: inputs.ManifestBytes,
            packageArchive: inputs.Archive);
    }

    internal async Task OpenAsync()
    {
        var activation = await Installed.Session().ApplyAsync(Installed.Prepared, default);
        Assert.True(activation.IsInstalled); Assert.True(activation.IsEnabled);
        Installed.Prepared.Dispose(); Installed.Verified.Dispose(); Installed.Root.Dispose();
        ExtensionProjectionPublication.PublishIfDirty(Catalog.Instance, Catalog.Database, Catalog.Lease, new(),
            Installed.Roots, Execution.Resources, Execution.Index);
        Frontend = Assert.IsType<InstalledFrontend>(Installed.Activator.RequirePublishedRuntime("game-checkin").Frontend);
    }

    internal async Task<JsonElement> Call(string operation, object payload, bool read = false, CancellationToken cancellation = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await Frontend.InvokeAsync(operation, read ? null : Guid.NewGuid(), "fixture:checkin-editor", false,
            JsonSerializer.SerializeToUtf8Bytes(payload), () => { }, deadline.Token, clientId: _clientId, clientLifetime: _client.Token);
        using var document = JsonDocument.Parse(result.Content);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), document.RootElement.ToString());
        return document.RootElement.GetProperty("value").Clone();
    }

    internal async Task<Guid> SaveNotification(Uri address)
    {
        var id = Guid.NewGuid(); var source = Execution.Source;
        var catalog = new NotificationCatalogService(source.Instance, source.Database, source.Lease, Execution.Resources, Execution.Index, new());
        using var url = new SecretValue(address.AbsoluteUri);
        var revision = catalog.Read().Revision;
        _ = await catalog.SaveAsync(Guid.NewGuid(), "local:checkin-notification", revision, id, 0,
            new("checkin " + id.ToString("N"), "", NotificationTaskKind.Webhook, true, NotificationChannelConfiguration.Default with {
                WebhookKind = NotificationWebhookKind.Generic, WebhookTemplate = "{\"text\":{text}}" }),
            [new(NotificationSecretField.WebhookUrl, 0, SecretDisposition.Set, url)]);
        return id;
    }

    internal async Task<ScheduledProviderState> CompletedSchedule(Guid task)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var values = new List<ScheduledProviderState>(); ScheduledProviderInventory.Visit(Catalog.Instance, Catalog.Database, new(), values.Add);
            var state = values.LastOrDefault(item => item.Source.Document.Task.OwnerTaskId == task);
            if (state?.Result is not null) { Assert.Equal("returned", state.Result.Completion); return state; }
            await Task.Delay(100, deadline.Token);
        }
    }

    internal async Task<ManualWorkState> Completed(Guid task, TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            var values = new List<ManualWorkState>(); ManualWorkInventory.Visit(Catalog.Instance, Catalog.Database, new(), values.Add);
            var state = values.LastOrDefault(item => item.Source.Document.Task.TaskId == task);
            if (state?.Result is not null) { Assert.Equal("returned", state.Result.Completion); return state; }
            await Task.Delay(100, deadline.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.Cancel();
        if (Logins is not null)
            foreach (var offer in Logins.Poll(Native!)) Logins.Complete(Native!, offer.CaptureId, offer.Nonce, "canceled", new Dictionary<string, SecretValue>());
        await Schedules.StopAsync(); await Installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10));
        await Notifications.DisposeAsync();
        if (Logins is not null) await Logins.DisposeAsync(); Clients?.Dispose();
        _http.Dispose(); _client.Dispose(); Installed.Dispose(); Catalog.Dispose(); Execution.Dispose();
    }
}

internal sealed class CheckInHttp(int responseCode) : HttpMessageHandler
{
    internal int Roles { get; private set; }
    internal int Submissions { get; private set; }
    internal string? Month { get; private set; }
    internal Action? BeforeSubmission { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("https://api.kurobbs.com", request.RequestUri!.GetLeftPart(UriPartial.Authority));
        Assert.Equal("synthetic-private-token", Assert.Single(request.Headers.GetValues("token")));
        Assert.Equal("android", Assert.Single(request.Headers.GetValues("source")));
        Assert.True(Guid.TryParse(Assert.Single(request.Headers.GetValues("devcode")), out _));
        var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&').Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
        Assert.Equal("3", form["gameId"]); byte[] content;
        switch (request.RequestUri.AbsolutePath)
        {
            case "/user/role/findRoleList":
                Roles++; content = JsonSerializer.SerializeToUtf8Bytes(new { code = 200, data = new[] { new { gameId = "3", roleId = "789123", serverId = "1", userId = "123456" } } }); break;
            case "/encourage/signIn/v2":
                Submissions++; BeforeSubmission?.Invoke(); Month = form["reqMonth"];
                Assert.Equal("789123", form["roleId"]); Assert.Equal("1", form["serverId"]); Assert.Equal("123456", form["userId"]);
                if (responseCode == 0) throw new HttpRequestException("fixture_lost_response");
                content = JsonSerializer.SerializeToUtf8Bytes(new { code = responseCode }); break;
            default: throw new IOException("unexpected_offline_fixture_request");
        }
        var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(content) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); return response;
    }
}
