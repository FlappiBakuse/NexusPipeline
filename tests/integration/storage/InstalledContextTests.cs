using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Media;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
public sealed class InstalledContextTests
{
    [Fact(DisplayName = "The Actual Fixed Context Reads Its Original Package And Allocates Only Its Own Runtime Output")]
    public async Task ScopesRuntimeOutputs()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "output");
        var result = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        Assert.True(result.IsInstalled); Assert.True(result.IsEnabled);
        using var activated = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.ActivationDirectory, "activated.json")));
        var path = activated.RootElement.GetProperty("outputPath").GetString()!;
        var owner = fixture.Package.Catalog;
        var root = Path.Combine(owner.Instance.RuntimeRoot, "extensions-data", "fixture-plugin", "activations");
        Assert.StartsWith(root + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fixture.ActivationNonce, File.ReadAllText(Path.Combine(path, "actual-output.txt")));
        var bytes = File.ReadAllBytes(Path.Combine(path, "actual-output.txt"));
        fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose();
        await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(path, "actual-output.txt")));
        Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "cleaned.json")));
    }

    [Theory(DisplayName = "The Formal Loader Has No Parameterless Fallback And Package Reads Cannot Escape Their Original Tree")]
    [InlineData("no-context")]
    [InlineData("asset-escape")]
    public async Task RejectsLoaderFallback(string mode)
    {
        using var fixture = new InstallRuntimeFixture(managedMode: mode);
        var failure = await Assert.ThrowsAnyAsync<Exception>(async () => await fixture.Session().ApplyAsync(fixture.Prepared, default));
        if (mode == "no-context")
            Assert.Contains(Flatten(failure), item => item.Message == "extension_managed_entry_invalid");
        Assert.False(File.Exists(Path.Combine(fixture.ActivationDirectory, "activated.json")));
        Assert.False(File.Exists(Path.Combine(fixture.ActivationDirectory, "resource.bin")));
        Assert.Null(fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan).Commit);
        Assert.Empty(fixture.Activator.ReadPublishedScopes());
    }

    [Fact(DisplayName = "A Published Media Registration Cannot Consume While The Actual Installer Mutation Still Owns The Final Commit")]
    public async Task WaitsForCommit()
    {
        var authorization = new MediaAuthorization();
        var media = new MediaPreviewRegistry(authorization);
        using var fixture = new InstallRuntimeFixture(managedMode: "media-wait",
            portDependencies: new(null, null, null, media, null, null));
        authorization.Current = new(fixture.Package.Catalog.Instance.InstanceId, Guid.NewGuid(), Guid.NewGuid());
        var observer = new BeforeSqlCommit(() =>
        {
            using var preview = media.Open(authorization.Current, "fixture-plugin", "held");
            var error = Assert.Throws<IOException>(() => preview.CaptureAsync().AsTask().GetAwaiter().GetResult());
            Assert.Equal("extension_consumption_resources_unavailable", error.Message);
            Assert.False(File.Exists(Path.Combine(fixture.ActivationDirectory, "media-entered")));
        });
        var result = await fixture.Session(observer).ApplyAsync(fixture.Prepared, default);
        Assert.True(observer.Fired); Assert.True(result.IsEnabled);
        fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose();
        await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = "An Actual Installed Async Media Callback Keeps Its Owned Resources Until The Callback Really Ends")]
    public async Task RetainsAsyncResources()
    {
        var authorization = new MediaAuthorization();
        var media = new MediaPreviewRegistry(authorization);
        using var fixture = new InstallRuntimeFixture(managedMode: "media-wait",
            portDependencies: new(null, null, null, media, null, null));
        authorization.Current = new(fixture.Package.Catalog.Instance.InstanceId, Guid.NewGuid(), Guid.NewGuid());
        _ = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose();
        using var preview = media.Open(authorization.Current, "fixture-plugin", "held");
        var capture = preview.CaptureAsync().AsTask();
        try
        {
            await WaitForFile(Path.Combine(fixture.ActivationDirectory, "media-entered"));
            await Assert.ThrowsAsync<TimeoutException>(() => fixture.Activator.StopAllAsync(TimeSpan.FromMilliseconds(100)));
            Assert.False(capture.IsCompleted);
            Assert.False(File.Exists(Path.Combine(fixture.ActivationDirectory, "cleaned.json")));
            Assert.Throws<IOException>(() => { using var denied = File.Open(Path.Combine(fixture.ActivationDirectory, "resource.bin"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
            using var deniedRoot = fixture.Package.Catalog.Resources.TryAcquire(Guid.NewGuid(),
                [new("extension:fixture-plugin", ResourceClaimMode.Mutation)]).Lease;
            Assert.Null(deniedRoot);
        }
        finally { File.WriteAllText(Path.Combine(fixture.ActivationDirectory, "media-release"), "release"); }
        await Assert.ThrowsAnyAsync<Exception>(async () => await capture);
        await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "cleaned.json")));
        using var closed = File.Open(Path.Combine(fixture.ActivationDirectory, "resource.bin"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(closed.Length > 0);
    }

    [Fact(DisplayName = "A Real Initial Callback That Ignores Cancellation Keeps Its Resources Until Its Actual Task Unwinds")]
    public async Task CollectsCancelledCallbacks()
    {
        using var fixture = new InstallRuntimeFixture(managedMode: "late");
        var running = fixture.Session().ApplyAsync(fixture.Prepared, default).AsTask();
        try
        {
            await WaitForFile(Path.Combine(fixture.ActivationDirectory, "activated.json"));
            await Assert.ThrowsAsync<TimeoutException>(() => fixture.Activator.StopAllAsync(TimeSpan.FromMilliseconds(100)));
            Assert.False(running.IsCompleted);
            Assert.False(File.Exists(Path.Combine(fixture.ActivationDirectory, "cleaned.json")));
            Assert.Throws<IOException>(() => { using var denied = File.Open(Path.Combine(fixture.ActivationDirectory, "resource.bin"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
        }
        finally { File.WriteAllText(Path.Combine(fixture.ActivationDirectory, "release"), "release"); }
        await Assert.ThrowsAnyAsync<Exception>(async () => await running);
        await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(Path.Combine(fixture.ActivationDirectory, "cleaned.json")));
        Assert.Null(fixture.Binding().Read(fixture.Accepted.JournalId, fixture.Accepted.Plan).Commit);
    }

    [Fact(DisplayName = "A Still Published Media Source Rejects The Actual Current Disabled Preference Before Entering The Plugin")]
    public async Task RechecksDisabledPreference()
    {
        var authorization = new MediaAuthorization();
        var media = new MediaPreviewRegistry(authorization);
        using var fixture = new InstallRuntimeFixture(managedMode: "media-wait",
            portDependencies: new(null, null, null, media, null, null));
        var owner = fixture.Package.Catalog;
        authorization.Current = new(owner.Instance.InstanceId, Guid.NewGuid(), Guid.NewGuid());
        var installed = await fixture.Session().ApplyAsync(fixture.Prepared, default);
        fixture.Prepared.Dispose(); fixture.Verified.Dispose(); fixture.Root.Dispose();
        using (var root = owner.Resources.TryAcquire(Guid.NewGuid(),
            [new("settings:host", ResourceClaimMode.Mutation)]).Lease)
        {
            Assert.NotNull(root);
            var store = new HostSettingsStore(owner.Database); var before = store.Read();
            var change = new HostSettingsChange(before.Revision, before.Values with { Extensions = [new("fixture-plugin", false)] });
            store.Commit(HostSettingsStore.Identity(root.OperationId, "local", change), change, DateTimeOffset.UtcNow);
        }
        Assert.True(fixture.Activator.IsPublished("fixture-plugin", installed.ActivationId!.Value));
        using var preview = media.Open(authorization.Current, "fixture-plugin", "held");
        await Assert.ThrowsAnyAsync<IOException>(async () => await preview.CaptureAsync());
        Assert.False(File.Exists(Path.Combine(fixture.ActivationDirectory, "media-entered")));
        await fixture.Activator.StopAllAsync(TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = "An Actual Secret Store From Another Instance Cannot Be Injected Into The Installed Port Factory")]
    public void RejectsForeignSecrets()
    {
        using var first = new InstallRuntimeFixture(savedEnabled: false);
        using var second = new InstallRuntimeFixture(savedEnabled: false);
        var owner = first.Package.Catalog; var other = second.Package.Catalog;
        var foreign = new SecretFieldService(other.Instance, other.Database, other.Lease, other.Resources, other.Index, other.Contracts);
        var error = Assert.Throws<IOException>(() => new InstalledPortFactory(owner.Instance, owner.Database,
            owner.Lease, owner.Resources, first.Activator, owner.Contracts, owner.Index, new(null, foreign, null, null, null, null)));
        Assert.Equal("secret_service_instance_unbound", error.Message);
    }

    private static IEnumerable<Exception> Flatten(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions)
                foreach (var nested in Flatten(inner)) yield return nested;
        else if (error.InnerException is { } inner)
            foreach (var nested in Flatten(inner)) yield return nested;
    }

    private static async Task WaitForFile(string path)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!File.Exists(path) && Environment.TickCount64 < deadline) await Task.Delay(20);
        Assert.True(File.Exists(path));
    }

    private sealed class MediaAuthorization : IRunMediaAuthorization
    {
        internal RunMediaContext Current = null!;
        public void AssertAllowed(RunMediaContext context, string extensionId)
        {
            if (Current is null || context != Current || extensionId != "fixture-plugin")
                throw new UnauthorizedAccessException("fixture_media_context_unbound");
        }
    }

    private sealed class BeforeSqlCommit(Action action) : IPayloadPersistenceObserver
    {
        internal bool Fired;
        public void Reached(PayloadPersistencePoint point, Guid journalId, long revision, JournalPhase phase)
        {
            if (Fired || point != PayloadPersistencePoint.JournalFlushed || phase != JournalPhase.Committed) return;
            Fired = true; action();
        }
    }
}
