using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Plugins.Managed;
using NexusPipeline.Modules.Scripts;
using Xunit;

namespace NexusPipeline.Tests.Scripts;

public sealed class ScriptTypeIconTests
{
    [Fact]
    public void PluginIconDeclarationAcceptsOnlyImmutableValidatedImageSources()
    {
        using var fixture = new Fixture();
        var source = new JsonObject
        {
            ["url"] = "https://raw.githubusercontent.com/official/repo/" + new string('a', 40) + "/icon.ico",
            ["sha256"] = new string('b', 64), ["contentType"] = "image/x-icon",
        };
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 2, ["name"] = "fixture", ["artifactName"] = "Fixture", ["version"] = "0.1.0",
            ["minHostVersion"] = "0.17.0", ["kind"] = "managed-code", ["apiVersion"] = "2.1",
            ["entryAssembly"] = "Fixture.dll", ["entryType"] = "Fixture.EntryPoint", ["scriptTypeIcon"] = source.DeepClone(),
        };
        string file = Path.Combine(fixture.Directory, "plugin.json");
        File.WriteAllText(file, manifest.ToJsonString());
        Assert.True(PluginManifest.TryLoad(fixture.Directory, out var loaded, out _));
        Assert.Equal(new Uri(source["url"]!.GetValue<string>()), loaded!.ScriptTypeIcon!.Uri);
        foreach (var invalid in new[]
        {
            ("url", "http://raw.githubusercontent.com/official/repo/" + new string('a', 40) + "/icon.ico"),
            ("url", "https://example.com/official/repo/" + new string('a', 40) + "/icon.ico"),
            ("url", "https://raw.githubusercontent.com/official/repo/main/icon.ico"),
            ("url", source["url"]!.GetValue<string>() + "?url=https://example.com"),
            ("url", "https://user@raw.githubusercontent.com/official/repo/" + new string('a', 40) + "/icon.ico"),
            ("sha256", "unverified"), ("contentType", "image/svg+xml"), ("contentType", "image/png"),
        })
        {
            var changed = (JsonObject)source.DeepClone(); changed[invalid.Item1] = invalid.Item2;
            manifest["scriptTypeIcon"] = changed; File.WriteAllText(file, manifest.ToJsonString());
            Assert.False(PluginManifest.TryLoad(fixture.Directory, out _, out _));
        }
    }

    [Fact]
    public async Task StartupWarmupPrimesPluginCacheWithBoundedSharedDownloads()
    {
        using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = fixture.Service(async (_, token) =>
        {
            if (Volatile.Read(ref fixture.Requests) == 3) started.TrySetResult();
            await release.Task.WaitAsync(token); return fixture.Response();
        });
        var sources = Enumerable.Range(0, 6).ToDictionary(index => "type-" + index, index => (PluginScriptTypeIcon?)fixture.Source("icon-" + index));
        sources.Add("without-icon", null);
        service.StartWarmup(sources);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, fixture.Requests);
        release.SetResult(); await service.WarmupTask;
        Assert.Equal(6, fixture.Requests);
        Assert.Equal(6, Directory.GetFiles(fixture.Directory, "*.image").Length);
        foreach (string typeId in sources.Keys.Where(key => key != "without-icon"))
            Assert.Equal(fixture.Bytes, (await service.GetAsync(typeId))!.Bytes);
        Assert.Equal((await service.GetAsync("general"))!.Bytes, (await service.GetAsync("without-icon"))!.Bytes);
        Assert.Equal(6, fixture.Requests);
        Assert.Null(await service.GetAsync("disabled-or-unknown"));
    }

    [Fact]
    public async Task ShutdownCancelsStartupDownloadsAndLeavesNoUnverifiedCache()
    {
        using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancelled = false;
        var service = fixture.Service(async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return fixture.Response();
        });
        service.StartWarmup(new Dictionary<string, PluginScriptTypeIcon?> { ["known"] = fixture.Source("icon") });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopWarmupAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancelled); Assert.True(service.WarmupTask.IsCompletedSuccessfully);
        Assert.Empty(Directory.GetFiles(fixture.Directory));
    }

    [Fact]
    public async Task GeneralAndUnknownTypesNeverRequestRemoteResources()
    {
        using var fixture = new Fixture();
        var service = fixture.Service((_, _) => throw new InvalidOperationException("Unexpected network request"));
        Assert.NotEmpty((await service.GetAsync("general"))!.Bytes);
        foreach (string type in new[] { "https://example.com/image.ico", "../general", "unknown", "general?url=x" })
            Assert.Null(await service.GetAsync(type));
        Assert.Equal(0, fixture.Requests);
    }

    [Fact]
    public async Task ConcurrentRequestsShareDownloadAndPersistVerifiedOfflineCache()
    {
        using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = fixture.Service(async (_, token) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
            return fixture.Response();
        });
        Task<ScriptTypeIcon?>[] requests = Enumerable.Range(0, 12).Select(_ => service.GetAsync("known")).ToArray();
        await started.Task;
        Assert.Equal(1, fixture.Requests);
        release.SetResult();
        foreach (ScriptTypeIcon? result in await Task.WhenAll(requests)) Assert.Equal(fixture.Bytes, result!.Bytes);
        var offline = fixture.Service((_, _) => throw new HttpRequestException("Offline"));
        Assert.Equal(fixture.Bytes, (await offline.GetAsync("known"))!.Bytes);
        Assert.Equal(1, fixture.Requests);
    }

    [Fact]
    public async Task CancellingOneClientKeepsOtherClientsSharedDownloadAlive()
    {
        using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = fixture.Service(async (_, token) => { started.SetResult(); await release.Task.WaitAsync(token); return fixture.Response(); });
        using var cancellation = new CancellationTokenSource();
        Task<ScriptTypeIcon?> departing = service.GetAsync("known", cancellation.Token);
        await started.Task;
        Task<ScriptTypeIcon?> remaining = service.GetAsync("known");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => departing);
        release.SetResult();
        Assert.Equal(fixture.Bytes, (await remaining)!.Bytes);
        Assert.Equal(1, fixture.Requests);
    }

    [Fact]
    public async Task ChangedCacheBytesAreReplacedOnlyByMatchingUpstreamBytes()
    {
        using var fixture = new Fixture();
        var first = fixture.Service((_, _) => Task.FromResult(fixture.Response()));
        await first.GetAsync("known");
        string cache = Assert.Single(Directory.GetFiles(fixture.Directory, "*.image"));
        File.WriteAllText(cache, "changed artwork");
        var second = fixture.Service((_, _) => Task.FromResult(fixture.Response()));
        Assert.Equal(fixture.Bytes, (await second.GetAsync("known"))!.Bytes);
        Assert.Equal(fixture.Bytes, File.ReadAllBytes(cache));
        Assert.Equal(2, fixture.Requests);
    }

    [Fact]
    public async Task FailedOrChangedDownloadsFallBackWithoutPersistingUnverifiedBytes()
    {
        using var fixture = new Fixture();
        var changed = fixture.Service((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }));
        byte[] project = (await changed.GetAsync("general"))!.Bytes;
        Assert.Equal(project, (await changed.GetAsync("known"))!.Bytes);
        var offline = fixture.Service((_, _) => throw new HttpRequestException("Offline"));
        Assert.Equal(project, (await offline.GetAsync("known"))!.Bytes);
        Assert.Empty(Directory.GetFiles(fixture.Directory));
    }

    [Fact]
    public async Task RedirectsAndOversizedBodiesCannotBecomeCachedArtwork()
    {
        using var fixture = new Fixture();
        var redirect = fixture.Service((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.com/untrusted.ico") } }));
        byte[] project = (await redirect.GetAsync("general"))!.Bytes;
        Assert.Equal(project, (await redirect.GetAsync("known"))!.Bytes);
        var oversized = fixture.Service((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2 * 1024 * 1024 + 1]) }));
        Assert.Equal(project, (await oversized.GetAsync("known"))!.Bytes);
        Assert.Equal(2, fixture.Requests);
        Assert.Empty(Directory.GetFiles(fixture.Directory));
    }

    [Fact]
    public async Task OptionalCacheWriteFailureStillReturnsVerifiedDownloadedIcon()
    {
        using var fixture = new Fixture();
        string blocked = Path.Combine(fixture.Directory, "blocked");
        File.WriteAllText(blocked, "owned fixture");
        var service = fixture.Service((_, _) => Task.FromResult(fixture.Response()), blocked);
        Assert.Equal(fixture.Bytes, (await service.GetAsync("known"))!.Bytes);
        Assert.Equal("owned fixture", File.ReadAllText(blocked));
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Directory = Path.Combine(Path.GetTempPath(), "nxp-type-icons-" + Guid.NewGuid().ToString("N"));
        internal readonly byte[] Bytes = Encoding.UTF8.GetBytes("frozen official artwork fixture");
        internal int Requests;
        private readonly List<ScriptTypeIconService> _services = new();
        internal Fixture() { System.IO.Directory.CreateDirectory(Directory); }
        internal HttpResponseMessage Response() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };
        internal PluginScriptTypeIcon Source(string path) => new(new Uri("https://raw.githubusercontent.com/official/repo/" + new string('a', 40) + "/" + path + ".ico"), Convert.ToHexStringLower(SHA256.HashData(Bytes)), "image/x-icon");
        internal ScriptTypeIconService Service(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, string? directory = null)
        {
            var service = new ScriptTypeIconService(directory ?? Directory, _ => new HttpClient(new Handler(async (request, token) => { Interlocked.Increment(ref Requests); return await send(request, token); })),
                new Dictionary<string, PluginScriptTypeIcon?>(StringComparer.OrdinalIgnoreCase) { ["known"] = Source("icon") });
            _services.Add(service); return service;
        }
        public void Dispose()
        {
            foreach (var service in _services) service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
