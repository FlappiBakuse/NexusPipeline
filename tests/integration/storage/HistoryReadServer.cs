using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Host.Control.Records;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Records;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class HistoryReadServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly InstanceLease _lease;
    internal string Root { get; }
    internal RuntimeInstance Instance { get; }
    internal StateDatabase Database { get; }
    internal Uri Address { get; }
    internal HttpClient Client { get; }

    private HistoryReadServer(WebApplication app, InstanceLease lease, string root,
        RuntimeInstance instance, StateDatabase database, Uri address)
    {
        _app = app;
        _lease = lease;
        Root = root;
        Instance = instance;
        Database = database;
        Address = address;
        Client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        { BaseAddress = address, Timeout = TimeSpan.FromSeconds(20) };
    }

    internal static async Task<HistoryReadServer> StartAsync()
    {
        var allowed = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")
            ?? throw new IOException("Fixture root required"));
        var root = Path.Combine(allowed, "history-read-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var contracts = new ContractReader();
        var instance = new InstanceRoot(contracts).OpenOrCreate(Path.Combine(root, "runtime"));
        var lease = InstanceLease.Acquire(instance, contracts);
        WebApplication? app = null;
        try
        {
            var database = StateDatabase.CreateFresh(instance);
            var index = new RecoveryIndex();
            var resources = new ResourceLeaseManager(new(index, TimeProvider.System));
            HistoryReadSeed.Populate(new(database));
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            app = builder.Build();
            app.Use(ControlExceptionBoundary.InvokeAsync);
            var authority = new ControlAuthentication(instance.InstanceId, false, null,
                () => new HostSettingsStore(database).Read(), null!, TimeProvider.System);
            app.Use(async (context, next) =>
            {
                if (!HttpMethods.IsGet(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    return;
                }
                await authority.InvokeAsync(context, next);
            });
            var useCases = new RecordUseCases(new RunRecordStore(database), new AttemptHistoryReader(database),
                new LogCatalogReader(instance, database, contracts), new ExecutionLogReader(instance, database, contracts),
                new ImageCatalogReader(instance, database, contracts),
                new ContentRetirementService(instance, database, contracts, lease, resources, index));
            new RecordControl(authority, useCases, contracts).Map(app);
            new RecordSummaryControl(authority, new(database), new(instance, database, contracts)).Map(app);
            await app.StartAsync();
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single());
            return new(app, lease, root, instance, database, address);
        }
        catch
        {
            if (app is not null) await app.DisposeAsync();
            lease.Dispose();
            throw;
        }
    }

    internal async Task CoordinateAsync()
    {
        var text = Environment.GetEnvironmentVariable("NEXUS_HISTORY_UI_TOKEN");
        if (text is null) return;
        if (!Guid.TryParseExact(text, "N", out var token)) throw new IOException("Invalid UI fixture token");
        var parent = Path.GetFullPath(Environment.GetEnvironmentVariable("NEXUS_TEST_ROOT")!);
        var prefix = Path.Combine(parent, "history-ui-" + token.ToString("N"));
        await using (var output = new FileStream(prefix + ".ready.json", FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            await JsonSerializer.SerializeAsync(output, new
            {
                instanceId = Instance.InstanceId, address = Address.AbsoluteUri, root = Root,
                records = HistoryReadSeed.Count, users = 10001, firstUserRuns = 90000,
                firstUserId = HistoryReadSeed.Id("20000000", 1), firstRunId = HistoryReadSeed.Id("10000000", 1),
                lastRunId = HistoryReadSeed.Id("10000000", HistoryReadSeed.Count),
                scope = "Production record routes, authentication and SQLite read-model fixture only",
                fullHostStartupQualified = false, completeExecutionJournals = 0
            }, JsonSerializerOptions.Web);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while (!File.Exists(prefix + ".complete")) await Task.Delay(100, timeout.Token);
        if (await File.ReadAllTextAsync(prefix + ".complete", timeout.Token) != token.ToString("N"))
            throw new IOException("UI fixture completion token differs");
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
        _lease.Dispose();
    }
}
