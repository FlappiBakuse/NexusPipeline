using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class ControlReadTests
{
    [Fact(DisplayName = "Actual Control Routes Return Effective Binding Sources And Owned Empty Extension History")]
    public async Task ReadsProjectionRoutes()
    {
        Guid bindingId = Guid.Empty;
        await using var fixture = await ControlHttpFixture.StartAsync((_, database, _, _, _, _) =>
        {
            var root = Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!),
                "control-projection-" + Guid.NewGuid().ToString("N"));
            var changes = AutomationDefinitionSeed.Create(root);
            bindingId = changes.Bindings[0].Id;
            var user = changes.Users[0] with { BindingOverrides = new()
            { General = new(true, false, 3, 2) } };
            changes = changes with { Users = changes.Users.SetItem(0, user),
                Bindings = changes.Bindings.SetItem(0, changes.Bindings[0] with { IsEnabled = true }) };
            new AutomationDefinitionStore(database).Commit(
                AutomationDefinitionStore.Identity(Guid.NewGuid(), "control-read-http-fixture", changes),
                changes, DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        });
        using var response = await fixture.Client.GetAsync($"control/v1/automation/bindings/{bindingId:D}/effective");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var binding = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(binding.RootElement.GetProperty("raw").GetProperty("isEnabled").GetBoolean());
        Assert.False(binding.RootElement.GetProperty("effective").GetProperty("isEnabled").GetBoolean());
        Assert.Equal("user", binding.RootElement.GetProperty("sources").GetProperty("isEnabled").GetString());
        using var historyResponse = await fixture.Client.GetAsync("control/v1/extensions/operations?offset=0&limit=20");
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        using var history = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
        Assert.Empty(history.RootElement.GetProperty("items").EnumerateArray());
        var missing = $"control/v1/extensions/catalog/stable/entries/fixture-plugin/details?generationId={Guid.NewGuid():D}" +
            $"&sequence=1&sha256={new string('0', 64)}";
        using var details = await fixture.Client.GetAsync(missing);
        Assert.Equal(HttpStatusCode.NotFound, details.StatusCode);
    }
    [Fact(DisplayName = "An Actual Uncached Extension Catalog Returns No Content Instead Of An Empty Json Success")]
    public async Task ReadsEmptyCatalog()
    {
        await using var fixture = await ControlHttpFixture.StartAsync((_, _, _, _, _, _) => Task.CompletedTask);
        foreach (var channel in new[] { "stable", "develop" })
        {
            using var response = await fixture.Client.GetAsync("control/v1/extensions/catalog/" + channel);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact(DisplayName = "Actual Host Secret Status And Read Work Locally And Are Denied From A Remote Network Interface")]
    public async Task ProtectsHostSecrets()
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        await using var fixture = await ControlHttpFixture.StartAsync(async (instance, database, contracts, lease, index, resources) =>
        {
            var settings = new HostSettingsStore(database);
            var current = settings.Read();
            var change = new HostSettingsChange(current.Revision, current.Values with
            { Service = current.Values.Service with { AllowRemoteAccess = true } });
            settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "control-read-http-fixture", change), change, DateTimeOffset.UtcNow);
            var secrets = new SecretFieldService(instance, database, lease, resources, index, contracts);
            var key = SecretFieldKey.HostControlBearer(instance.InstanceId);
            var before = await secrets.GetStateAsync(key, CancellationToken.None);
            using var value = new SecretValue(token);
            await secrets.ApplyAsync(Guid.NewGuid(), key, before.Revision, SecretDisposition.Set, value, CancellationToken.None);
        });

        using var statusResponse = await fixture.Client.GetAsync("control/v1/local/secrets/control-bearer");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
        Assert.True(status.RootElement.GetProperty("isConfigured").GetBoolean());
        Assert.True(status.RootElement.GetProperty("isReadable").GetBoolean());

        using var body = new ByteArrayContent([]);
        using var readResponse = await fixture.Client.PostAsync("control/v1/local/secrets/control-bearer/read", body);
        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        Assert.Equal(token, await readResponse.Content.ReadAsStringAsync());

        const string proxyPassword = "fixture-local-proxy-password";
        using var proxyContent = new StringContent(proxyPassword, Encoding.UTF8, "text/plain");
        using var proxyChange = new HttpRequestMessage(HttpMethod.Post, "control/v1/local/secrets/proxy-password/change")
        { Content = proxyContent };
        proxyChange.Headers.Add("Nexus-Operation-Id", Guid.NewGuid().ToString("D"));
        proxyChange.Headers.Add("Nexus-Expected-Revision", "0");
        proxyChange.Headers.Add("Nexus-Secret-Disposition", "set");
        using var proxySaved = await fixture.Client.SendAsync(proxyChange);
        Assert.Equal(HttpStatusCode.OK, proxySaved.StatusCode);
        using var proxyStatus = await fixture.Client.GetAsync("control/v1/local/secrets/proxy-password");
        using var proxyState = JsonDocument.Parse(await proxyStatus.Content.ReadAsStringAsync());
        Assert.True(proxyState.RootElement.GetProperty("isConfigured").GetBoolean());
        using var proxyReadBody = new ByteArrayContent([]);
        using var proxyRead = await fixture.Client.PostAsync("control/v1/local/secrets/proxy-password/read", proxyReadBody);
        Assert.Equal(proxyPassword, await proxyRead.Content.ReadAsStringAsync());

        var remoteAddress = NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up &&
                item.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Select(item => item.Address)
            .First(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));
        using var remote = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        { BaseAddress = new UriBuilder(fixture.Address) { Host = remoteAddress.ToString() }.Uri };
        remote.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var remoteStatus = await remote.GetAsync("control/v1/local/secrets/control-bearer");
        Assert.Equal(HttpStatusCode.Forbidden, remoteStatus.StatusCode);
        using var remoteBody = new ByteArrayContent([]);
        using var remoteRead = await remote.PostAsync("control/v1/local/secrets/control-bearer/read", remoteBody);
        Assert.Equal(HttpStatusCode.Forbidden, remoteRead.StatusCode);
        Assert.DoesNotContain(token, await remoteRead.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var remoteChangeContent = new StringContent("remote-secret-must-not-be-stored", Encoding.UTF8, "text/plain");
        using var remoteChange = new HttpRequestMessage(HttpMethod.Post, "control/v1/local/secrets/proxy-password/change")
        { Content = remoteChangeContent };
        remoteChange.Headers.Add("Nexus-Operation-Id", Guid.NewGuid().ToString("D"));
        remoteChange.Headers.Add("Nexus-Expected-Revision", "1");
        remoteChange.Headers.Add("Nexus-Secret-Disposition", "set");
        using var remoteWrite = await remote.SendAsync(remoteChange);
        Assert.Equal(HttpStatusCode.Forbidden, remoteWrite.StatusCode);
        using var verifyProxyBody = new ByteArrayContent([]);
        using var verifyProxy = await fixture.Client.PostAsync("control/v1/local/secrets/proxy-password/read", verifyProxyBody);
        Assert.Equal(proxyPassword, await verifyProxy.Content.ReadAsStringAsync());
    }

    [Fact(DisplayName = "Actual Host Exposes The Running Attempt And Streams Its Main Live Log")]
    public async Task StreamsRunLogs()
    {
        var marker = Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!),
            "control-live-log-" + Guid.NewGuid().ToString("N"));
        Guid bindingId = Guid.NewGuid();
        await using var fixture = await ControlHttpFixture.StartAsync((_, database, _, _, _, _) =>
        {
            var worker = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!);
            var root = new AutomationRoot(Guid.NewGuid(), 0, "Live HTTP worker", AutomationRootKind.External, Path.GetDirectoryName(worker)!);
            var script = new AutomationScript(Guid.NewGuid(), 0, "Live HTTP worker", AutomationKind.Generic, root.Id, null,
                new(root.Id, Path.GetFileName(worker)), ["process-output", "wait", marker], [], [], new(1, 30, 120),
                new(AutomationJudgementLanguage.Keywords, ["never-finish"], [], null)) { UpdateConfigurationAtFinish = false };
            var user = new AutomationUser(Guid.NewGuid(), 0, "Live HTTP user", 0);
            var binding = new AutomationBinding(bindingId, 0, user.Id, script.Id, true, 0) { NotifyEnabled = false };
            var changes = new AutomationChanges(0, [root], [script], [user], [binding], [], [], []);
            new AutomationDefinitionStore(database).Commit(
                AutomationDefinitionStore.Identity(Guid.NewGuid(), "control-read-http-fixture", changes), changes, DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        });

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Guid? startOperation = null;
        Guid? runId = null;
        Guid? attemptId = null;
        try
        {
            using var catalogResponse = await fixture.Client.GetAsync("control/v1/automation", deadline.Token);
            catalogResponse.EnsureSuccessStatusCode();
            using var catalog = JsonDocument.Parse(await catalogResponse.Content.ReadAsStringAsync(deadline.Token));
            using var settingsResponse = await fixture.Client.GetAsync("control/v1/settings/service", deadline.Token);
            settingsResponse.EnsureSuccessStatusCode();
            using var settings = JsonDocument.Parse(await settingsResponse.Content.ReadAsStringAsync(deadline.Token));
            startOperation = Guid.NewGuid();
            var selection = new
            {
                format = "nexus.control.execution-selection", version = 1,
                target = new { kind = "binding", bindingId },
                expectedCatalogRevision = catalog.RootElement.GetProperty("catalogRevision").GetInt64(),
                expectedSettingsRevision = settings.RootElement.GetProperty("revision").GetInt64()
            };
            using var startResponse = await fixture.Client.PostAsJsonAsync(
                $"control/v1/execution/start/{startOperation:D}", selection, deadline.Token);
            Assert.Equal(HttpStatusCode.Accepted, startResponse.StatusCode);
            using var accepted = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync(deadline.Token));
            runId = accepted.RootElement.GetProperty("runId").GetGuid();
            attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

            await WaitForFile(marker + ".output-ready", deadline.Token);
            using var active = await ReadActive(fixture.Client, runId.Value, deadline.Token);
            Assert.Equal(runId.Value, Assert.Single(active.RootElement.GetProperty("items").EnumerateArray())
                .GetProperty("runId").GetGuid());
            var stepId = active.RootElement.GetProperty("items")[0].GetProperty("steps")[0].GetProperty("stepId").GetString()!;
            var path = $"control/v1/execution/runs/{runId:D}/attempts/{attemptId:D}/logs/{Uri.EscapeDataString(stepId)}/main/events";
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var streamResponse = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
            Assert.Equal("text/event-stream", streamResponse.Content.Headers.ContentType?.MediaType);
            await using var stream = await streamResponse.Content.ReadAsStreamAsync(deadline.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
            var line = await reader.ReadLineAsync(deadline.Token);
            Assert.StartsWith("data: ", line, StringComparison.Ordinal);
            using var message = JsonDocument.Parse(line![6..]);
            Assert.Equal("tail", message.RootElement.GetProperty("kind").GetString());
            Assert.Equal(runId.Value, message.RootElement.GetProperty("key").GetProperty("runId").GetGuid());
            Assert.Equal(attemptId.Value, message.RootElement.GetProperty("key").GetProperty("attemptId").GetGuid());
            Assert.Equal(stepId, message.RootElement.GetProperty("key").GetProperty("stepId").GetString());
            Assert.Equal("main", message.RootElement.GetProperty("key").GetProperty("role").GetString());
            var tail = message.RootElement.GetProperty("data");
            Assert.NotEmpty(tail.GetProperty("frames").EnumerateArray());
            var bytes = tail.GetProperty("frames").EnumerateArray()
                .Select(frame => Convert.FromBase64String(frame.GetProperty("bytes").GetString()!)).SelectMany(value => value).ToArray();
            Assert.Contains("ready\n", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        }
        finally
        {
            if (startOperation is { } operation && runId is { } run && attemptId is { } attempt && !fixture.Host.HasExited)
                await Cancel(fixture.Client, operation, run, attempt, CancellationToken.None);
        }
    }

    private static async Task<JsonDocument> ReadActive(HttpClient client, Guid runId, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            using var response = await client.GetAsync("control/v1/execution/active", cancellation);
            response.EnsureSuccessStatusCode();
            var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            if (document.RootElement.GetProperty("items").EnumerateArray()
                .Any(item => item.GetProperty("runId").GetGuid() == runId)) return document;
            document.Dispose();
            await Task.Delay(25, cancellation);
        }
    }

    private static async Task WaitForFile(string path, CancellationToken cancellation)
    {
        while (!File.Exists(path)) await Task.Delay(20, cancellation);
    }

    private static async Task Cancel(HttpClient client, Guid startOperation, Guid runId, Guid attemptId, CancellationToken cancellation)
    {
        var cancelOperation = Guid.NewGuid();
        var request = new
        {
            format = "nexus.control.execution-cancellation", version = 1, runId, attemptId
        };
        using var response = await client.PostAsJsonAsync(
            $"control/v1/execution/{startOperation:D}/cancel/{cancelOperation:D}", request, cancellation);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ControlHttpFixture : IAsyncDisposable
{
    private readonly Process _host;
    private readonly Task<string> _errors;
    internal Process Host => _host;
    internal Uri Address { get; }
    internal HttpClient Client { get; }

    private ControlHttpFixture(Process host, Task<string> errors, Uri address)
    {
        _host = host; _errors = errors; Address = address;
        Client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(20) };
    }

    internal static async Task<ControlHttpFixture> StartAsync(
        Func<RuntimeInstance, StateDatabase, ContractReader, InstanceLease, RecoveryIndex, ResourceLeaseManager, Task> prepare)
    {
        var owner = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")
            ?? throw new InvalidOperationException("NEXUS_TEST_ROOT is required"));
        Directory.CreateDirectory(owner);
        var scope = Path.Combine(owner, "control-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scope);
        var runtime = Path.Combine(scope, "runtime");
        var hostExecutable = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")
            ?? throw new InvalidOperationException("NEXUS_TEST_HOST_EXE is required"));
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(runtime);
        var database = StateDatabase.CreateFresh(instance);
        using (var lease = InstanceLease.Acquire(instance, contracts))
        {
            var roots = new PayloadApplyRoots(Path.GetDirectoryName(hostExecutable)!, Path.Combine(runtime, "extensions"));
            var index = new StartupJournalRecovery(instance, contracts, database, roots).Recover(lease);
            var resources = new ResourceLeaseManager(new(index, TimeProvider.System));
            await prepare(instance, database, contracts, lease, index, resources);
            if (new HostSettingsStore(database).Read().Values.Service.AllowRemoteAccess &&
                Environment.GetEnvironmentVariable("NEXUS_TEST_REMOTE_ACCESS") != "1")
                throw new IOException("NEXUS_TEST_REMOTE_ACCESS_required_for_lan_test");
        }

        var start = new ProcessStartInfo(hostExecutable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.Environment.Remove("DOTNET_STARTUP_HOOKS");
        foreach (var value in new[] { "serve", "--runtime-root", runtime, "--port", "0" }) start.ArgumentList.Add(value);
        var host = Process.Start(start) ?? throw new IOException("control_http_host_not_started");
        var errors = host.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var line = await host.StandardOutput.ReadLineAsync(deadline.Token);
            if (string.IsNullOrEmpty(line)) throw new IOException("control_http_host_not_ready: " + await errors);
            using var ready = JsonDocument.Parse(line);
            if (ready.RootElement.GetProperty("kind").GetString() != "host.ready")
                throw new IOException("control_http_host_identity_invalid");
            return new(host, errors, new Uri(ready.RootElement.GetProperty("address").GetString()!));
        }
        catch
        {
            if (!host.HasExited) { host.Kill(entireProcessTree: true); await host.WaitForExitAsync(); }
            host.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (!_host.HasExited)
        {
            _host.Kill(entireProcessTree: true);
            await _host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        _ = await _errors;
        _host.Dispose();
    }
}
