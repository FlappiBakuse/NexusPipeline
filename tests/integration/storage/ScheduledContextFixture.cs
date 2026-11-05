using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class ScheduledContextFixture : IAsyncDisposable
{
    internal ExecutionAdmissionFixture Base { get; } = new();
    internal PersistentScheduleService Schedules { get; }
    internal InstallRuntimeFixture Installed { get; }
    private readonly Clock _clock = new();

    internal ScheduledContextFixture()
    {
        var source = Base.Source;
        Schedules = new(source.Instance, source.Database, source.Lease, Base.Resources, Base.Lifecycle, Base.Index,
            Base.Service, _clock);
        var catalog = Base.CreateCatalog();
        Installed = new(managedMode: "schedule-wait", catalog: catalog,
            portDependencies: new(Schedules, null, null, null, null, null), managedPayloadEdit: directory =>
            {
                var path = Path.Combine(directory, "activation-input.json");
                var node = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
                node["ScheduledTaskId"] = Guid.NewGuid().ToString("D");
                node["ScheduledUtc"] = _clock.GetUtcNow();
                File.WriteAllBytes(path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(node));
            });
    }

    internal string Marker(string name) => Path.Combine(Installed.ActivationDirectory, name);
    internal void Release(string name) => File.WriteAllText(Marker(name), "release");
    internal async Task Start()
    {
        _ = await Installed.Session().ApplyAsync(Installed.Prepared, default);
        Installed.Prepared.Dispose(); Installed.Verified.Dispose(); Installed.Root.Dispose();
        ExtensionProjectionPublication.PublishIfDirty(Base.Source.Instance, Base.Source.Database, Base.Source.Lease,
            Installed.Package.Catalog.Contracts, Installed.Roots, Base.Resources, Base.Index);
        Schedules.Start();
        await WaitFile("scheduled-entered");
    }
    internal void Disable()
    {
        using var root = Base.Resources.TryAcquire(Guid.NewGuid(),
            [new("settings:host", NexusPipeline.Core.Maintenance.ResourceClaimMode.Mutation)]).Lease;
        Assert.NotNull(root);
        var settings = new HostSettingsStore(Base.Source.Database); var before = settings.Read();
        var change = new HostSettingsChange(before.Revision, before.Values with { Extensions = [new("fixture-plugin", false)] });
        _ = settings.Commit(HostSettingsStore.Identity(root.OperationId, "local", change), change, DateTimeOffset.UtcNow);
    }
    internal ScheduledProviderState State()
    {
        var result = new List<ScheduledProviderState>();
        ScheduledProviderInventory.Visit(Base.Source.Instance, Base.Source.Database, new(), result.Add);
        return Assert.Single(result);
    }
    internal async Task WaitResult()
    {
        var deadline = Environment.TickCount64 + 10000;
        while (State().Result is null && Environment.TickCount64 < deadline) await Task.Delay(20);
        Assert.NotNull(State().Result);
    }
    internal async Task WaitFile(string name)
    {
        var deadline = Environment.TickCount64 + 10000;
        while (!File.Exists(Marker(name)) && Environment.TickCount64 < deadline) await Task.Delay(20);
        Assert.True(File.Exists(Marker(name)));
    }
    public async ValueTask DisposeAsync()
    {
        Release("scheduled-release"); Release("scheduled-late-release");
        try { await Schedules.StopAsync(); }
        finally
        {
            try { await Installed.Activator.StopAllAsync(TimeSpan.FromSeconds(10)); }
            finally { Installed.Dispose(); Base.Dispose(); }
        }
    }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2032, 6, 7, 12, 10, 0, TimeSpan.Zero);
    }
}
