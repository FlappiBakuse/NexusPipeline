using System.Runtime.Versioning;
using System.Text;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Settings;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.TestFixtures;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
[Collection("SerialNative")]
public sealed class StoreNetworkTests
{
    [Fact(DisplayName = "A New Store Client Uses The Actual Saved Proxy And Credential While An Inflight Client Keeps Its Original Copy")]
    public async Task FreezesClientCredentials()
    {
        using var fixture = new ExtensionCatalogFixture();
        var settings = Settings(fixture); var secrets = Secrets(fixture);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string user = "store-proxy-fixture";
        var firstAuthorization = Authorization(user, "store-first-canary");
        var secondAuthorization = Authorization(user, "store-second-canary");
        await using var first = new NativeHttpFixture(async (request, cancellation) =>
        {
            if (!request.Headers.TryGetValue("Proxy-Authorization", out var value) || value != firstAuthorization) return Challenge();
            arrived.TrySetResult(); await release.Task.WaitAsync(cancellation);
            return new NativeHttpReply(200, "first"u8.ToArray());
        });
        await using var second = new NativeHttpFixture(request =>
            request.Headers.TryGetValue("Proxy-Authorization", out var value) && value == secondAuthorization
                ? new NativeHttpReply(200, "second"u8.ToArray()) : Challenge());
        Save(settings, new(ProxyMode.Http, first.Address.AbsoluteUri, user));
        using (var value = new SecretValue("store-first-canary"))
            await secrets.ApplyAsync(Guid.NewGuid(), SecretFieldKey.HostProxyPassword(fixture.Instance.InstanceId), 0, SecretDisposition.Set, value, default);
        var network = new ExtensionStoreNetwork(fixture.Instance, fixture.Database, fixture.Lease, fixture.Resources,
            () => settings.Read().Effective.Proxy, secrets);
        using var original = network.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var inFlight = original.GetStringAsync("http://nexus-store-proxy-fixture.invalid/first", timeout.Token);
        try
        {
            await arrived.Task.WaitAsync(timeout.Token);
            Save(settings, new(ProxyMode.Http, second.Address.AbsoluteUri, user));
            using (var value = new SecretValue("store-second-canary"))
                await secrets.ApplyAsync(Guid.NewGuid(), SecretFieldKey.HostProxyPassword(fixture.Instance.InstanceId), 1, SecretDisposition.Set, value, default);
            using var current = network.CreateClient();
            Assert.Equal("second", await current.GetStringAsync("http://nexus-store-proxy-fixture.invalid/current", timeout.Token));
        }
        finally { release.TrySetResult(); }
        Assert.Equal("first", await inFlight);
        Assert.Equal("first", await original.GetStringAsync("http://nexus-store-proxy-fixture.invalid/original", timeout.Token));
        Assert.Contains(first.Requests, item => item.Headers.TryGetValue("Proxy-Authorization", out var value) && value == firstAuthorization);
        Assert.Contains(second.Requests, item => item.Headers.TryGetValue("Proxy-Authorization", out var value) && value == secondAuthorization);
    }

    [Fact(DisplayName = "New Store Work Cannot Bypass Actual Network Claims Or Turn An Unreadable Configured Password Into Empty Credentials")]
    public async Task RequiresNetworkClaims()
    {
        using var fixture = new ExtensionCatalogFixture(); var settings = Settings(fixture); var secrets = Secrets(fixture);
        Save(settings, new(ProxyMode.Http, "http://127.0.0.1:49151/", "fixture"));
        using (var value = new SecretValue("store-unreadable-canary"))
            await secrets.ApplyAsync(Guid.NewGuid(), SecretFieldKey.HostProxyPassword(fixture.Instance.InstanceId), 0, SecretDisposition.Set, value, default);
        var reads = 0;
        var network = new ExtensionStoreNetwork(fixture.Instance, fixture.Database, fixture.Lease, fixture.Resources,
            () => { reads++; return settings.Read().Effective.Proxy; }, secrets);
        using (var mutation = fixture.Resources.TryAcquire(Guid.NewGuid(), [new("settings:host", ResourceClaimMode.Mutation)]).Lease!)
        {
            Assert.NotNull(mutation);
            Assert.Equal("extension_store_network_busy", Assert.Throws<IOException>(() => network.CreateClient()).Message);
            Assert.Equal(0, reads);
        }
        var path = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Instance.RuntimeRoot, "secrets", "payloads")));
        var original = File.ReadAllBytes(path); var changed = original.ToArray(); changed[0] ^= 1;
        File.WriteAllBytes(path, changed);
        try { Assert.Equal("secret_unavailable", Assert.Throws<IOException>(() => network.CreateClient()).Message); }
        finally { File.WriteAllBytes(path, original); }
        using var client = network.CreateClient(); Assert.Equal(2, reads);
    }

    private static HostSettingsService Settings(ExtensionCatalogFixture fixture)
    {
        var value = new HostSettingsStore(fixture.Database).Read().Values;
        return new(fixture.Instance, fixture.Database, fixture.Lease, fixture.Resources,
            new(value.Desktop.Mode, value.Service, value.Updates.PluginSource));
    }
    private static SecretFieldService Secrets(ExtensionCatalogFixture fixture) =>
        new(fixture.Instance, fixture.Database, fixture.Lease, fixture.Resources, fixture.Index, fixture.Contracts);
    private static void Save(HostSettingsService settings, ProxyPreferences proxy)
    {
        var current = settings.Read().Saved; var change = new HostSettingsChange(current.Revision, current.Values with { Proxy = proxy });
        settings.Save(HostSettingsStore.Identity(Guid.NewGuid(), "local", change), change, DateTimeOffset.UtcNow);
    }
    private static string Authorization(string user, string password) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
    private static NativeHttpReply Challenge() => new(407, [], new Dictionary<string, string> { ["Proxy-Authenticate"] = "Basic realm=fixture" });
}
