using System.Net;
using System.Runtime.Versioning;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Operations;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Platform.Storage.Operations;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using System.Text.Json;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Host.Control.Http;
using NexusPipeline.Host.Lifecycle;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Settings;

namespace NexusPipeline.Host.Composition;

internal static class ApplicationHost
{
    [SupportedOSPlatform("windows")]
    public static async Task<int> RunAsync(string runtimeRoot, int? requestedPort, int? nativeClientProcessId = null,
        InstanceLease? startupLease = null, NexusPipeline.Platform.Processes.MaintenanceStartupOrigin? maintenanceOrigin = null,
        bool launchDesktop = false, UpdateWindowIntent? resumeWindow = null, string? installationBundle = null)
    {
        using var externalNativeBinding = nativeClientProcessId is { } nativePid ? new NativeClientBinding(nativePid) : null;
        var contracts = new ContractReader();
        var instance = startupLease?.Instance ?? new InstanceRoot(contracts).OpenOrCreate(runtimeRoot);
        using var instanceLease = startupLease ?? InstanceLease.Acquire(instance, contracts);
        instanceLease.VerifyHeld(instance);
        var payloadRoots = new PayloadApplyRoots(Path.GetDirectoryName(
            Platform.Processes.ProcessIdentityReader.Read(Environment.ProcessId).ImagePath)!, Path.Combine(instance.RuntimeRoot, "extensions"));
        var runtime = RuntimeReadiness.Verify(instance, contracts, instanceLease, payloadRoots);
        Directory.CreateDirectory(PhysicalPathPolicy.NormalizeWithoutLinks(payloadRoots.ExtensionPayloadRoot));
        var lifecycle = new HostLifecycle(runtime.Recovery, TimeProvider.System);
        var resources = new ResourceLeaseManager(lifecycle);
        var controlSettings = new NexusPipeline.Platform.Storage.Sqlite.HostSettingsStore(runtime.Database);
        var savedStartup = controlSettings.Read().Values;
        var controlStartup = savedStartup.Service;
        var port = requestedPort ?? controlStartup.WebPort;
        int? mcpPort = controlStartup.McpEnabled ? controlStartup.McpPort : null;
        if (port != 0 && port == mcpPort) throw new ArgumentException("control_listener_ports_conflict");
        using var listener = new ControlListener(controlStartup.AllowRemoteAccess ? IPAddress.Any : IPAddress.Loopback, port, mcpPort);
        port = listener.Port;
        var windowIntent = resumeWindow ?? (launchDesktop ? new UpdateWindowIntent(
            savedStartup.Desktop.Mode == NexusPipeline.Core.Settings.DesktopMode.Desktop ? UpdateWindowMode.Desktop : UpdateWindowMode.Browser,
            savedStartup.Desktop.OpenOnStartup, "/") : null);
        using var desktopSigner = new NexusPipeline.Platform.Security.DesktopResponseSigner();
        using var ownedDesktop = windowIntent is not null && windowIntent.Mode != UpdateWindowMode.Headless ?
            new NexusPipeline.Host.Desktop.OwnedDesktop(instance, payloadRoots.HostPayloadRoot, port, desktopSigner,
                windowIntent, savedStartup.Desktop.MinimizeToTray) : null;
        var nativeBinding = ownedDesktop?.Binding ?? externalNativeBinding;
        new RecoveryCompletionService(instance, runtime.Database, contracts, instanceLease, resources, runtime.Recovery).ReconcilePending();
        var providers = new ExecutionProviderRegistry();
        providers.RestoreSavedReferences(instance, runtime.Database, contracts, instanceLease, resources);
        var secretService = new SecretFieldService(instance, runtime.Database, instanceLease, resources, runtime.Recovery, contracts);
        var secrets = new SecretsControl(secretService);
        var settingsService = HostStartupSettings.Create(instance, runtime.Database, instanceLease, resources, runtime.Recovery);
        var updateNetwork = new ExtensionStoreNetwork(instance, runtime.Database, instanceLease, resources,
            () => settingsService.Read().Effective.Proxy, secretService);
        await using var updates = new Platform.Storage.Maintenance.HostUpdateService(instance, runtime.Database, instanceLease,
            resources, contracts, payloadRoots,
            Platform.Storage.Maintenance.UpdateReleaseTrust.Configured(), updateNetwork.CreateClient,
            () => settingsService.Read().Effective.Updates.SourceUrl);
        await using var extensions = new ExtensionRuntimeLifecycle(instance, runtime.Database, instanceLease, contracts,
            payloadRoots,
            resources, runtime.Recovery, providers, ExtensionReleaseTrust.Configured());
        await using var packages = new ExtensionPackageService(instance, runtime.Database, instanceLease, contracts,
            payloadRoots,
            resources, runtime.Recovery, extensions, () => settingsService.Read().Effective.Proxy, secretService);
        var operations = new OperationQueryService(instance, instanceLease, runtime.Database);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = payloadRoots.HostPayloadRoot });
        await using var services = new ExecutionRuntimeServices(instance, runtime.Database, contracts, instanceLease, resources, lifecycle, runtime.Recovery, providers, secretService, nativeBinding);
        var extensionSecrets = new SecretAccessPolicy(instance, runtime.Database, instanceLease, resources);
        var desktopPresence = new NexusPipeline.Core.Desktop.DesktopPresence();
        using var nativeClients = new NexusPipeline.Core.Desktop.NativeClientSessions(instance.InstanceId);
        await using var browserLogins = new NexusPipeline.Core.Desktop.BrowserLoginSessions();
        extensions.ConfigurePorts(new(services.Scheduling, secretService, extensionSecrets, services.LiveMedia.Previews,
            services.LiveMedia, services.LiveMedia, updateNetwork.CreateClient, desktopPresence, nativeClients, browserLogins));
        var taskAdapters = extensions.CreateAdapterPort(extensionSecrets);
        var projectCompilation = new ProjectCompilationService(instance, runtime.Database, instanceLease, resources, contracts, payloadRoots, extensions.Activator,
            new NexusPipeline.Platform.Storage.Secrets.SecretDocumentService(instance, runtime.Database, instanceLease, resources,
                runtime.Recovery, contracts, secretService: secretService));
        await using var projectPreviews = new ProjectPreviewService(instance, runtime.Database, instanceLease, resources, contracts, payloadRoots, extensions.Activator);
        services.Admission.ConfigureTaskAdapters(taskAdapters); services.Consumer.ConfigureTaskAdapters(taskAdapters);
        var emulators = new NexusPipeline.Platform.Storage.Emulation.EmulatorTargetService(instance, runtime.Database, contracts,
            instanceLease, resources, payloadRoots, extensions.Activator);
        services.Admission.ConfigureEmulators(emulators);
        builder.Services.AddSingleton<ITaskAdapterPort>(taskAdapters);
        var execution = services.Consumer;
        var configurationValues = new NexusPipeline.Host.Control.Configuration.ValueUseCases(
            new ValueService(instance, runtime.Database, contracts, instanceLease, resources));
        var configurationImports = new NexusPipeline.Host.Control.Configuration.ImportUseCases(
            new ConfigurationImportService(instance, runtime.Database, contracts, instanceLease, resources, providers: providers), contracts);
        var executionControlUseCases = new NexusPipeline.Host.Control.Execution.ExecutionUseCases(services.Admission,
            new ExecutionControlService(instance, runtime.Database, contracts, instanceLease, services.Admission, services.Retry, services.Consumer),
            services.Authorization);
        var definitions = new AutomationDefinitionService(instance, contracts, runtime.Database, instanceLease, resources, runtime.Recovery, payloadRoots);
        builder.Services.AddSingleton(definitions);
        var automationControl = new NexusPipeline.Host.Control.Automation.AutomationUseCases(definitions, new AutomationBundleCodec(contracts));
        var automationImports = new AutomationImportService(instance, contracts, runtime.Database, instanceLease, resources, runtime.Recovery, payloadRoots);
        builder.Services.AddSingleton(automationImports);
        builder.Services.AddSingleton(services.Admission);
        builder.Services.AddSingleton(execution);
        builder.Services.AddSingleton<IExtensionRuntimeLifecycle>(extensions);
        builder.Services.AddSingleton<IExtensionPackages>(packages);
        builder.Services.AddSingleton<IOperationQueries>(operations);
        var extensionOperations = new ExtensionQueryService(instance, instanceLease, runtime.Database, contracts,
            payloadRoots);
        builder.Services.AddSingleton<IExtensionOperationQueries>(extensionOperations);
        builder.Services.AddSingleton(new ConfigurationRevisionService(instance, runtime.Database, contracts, instanceLease, resources));
        builder.Services.AddSingleton(new ConfigurationBatchService(instance, runtime.Database, contracts, instanceLease, resources));
        builder.Services.AddSingleton(new ConfigurationPatchService(instance, runtime.Database, contracts, instanceLease, resources));
        builder.Services.AddSingleton(new PatchBatchService(instance, runtime.Database, contracts, instanceLease, resources));
        builder.Services.AddSingleton(new EditCompletionService(instance, runtime.Database, contracts, instanceLease, resources, runtime.Recovery));
        builder.Services.AddSingleton(new ConfigurationRunPreparation(instance, runtime.Database, contracts, instanceLease, resources));
        builder.Services.AddSingleton(new RunCompletionService(instance, runtime.Database, contracts, instanceLease, resources, runtime.Recovery));
        using var configurationEdits = new ConfigurationEditController(instance, runtime.Database, contracts, instanceLease, resources, runtime.Recovery, activator: extensions.Activator);
        builder.Services.AddSingleton(configurationEdits);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.AddFilter((category, level) =>
        {
            var minimum = settingsService.EffectiveLogLevel switch
            {
                NexusPipeline.Core.Settings.HostLogLevel.Debug => LogLevel.Debug,
                NexusPipeline.Core.Settings.HostLogLevel.Info => LogLevel.Information,
                NexusPipeline.Core.Settings.HostLogLevel.Warning => LogLevel.Warning,
                NexusPipeline.Core.Settings.HostLogLevel.Error => LogLevel.Error,
                NexusPipeline.Core.Settings.HostLogLevel.Fatal => LogLevel.Critical,
                _ => LogLevel.None
            };
            if (category?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true && minimum < LogLevel.Warning)
                minimum = LogLevel.Warning;
            return level >= minimum;
        });
        builder.WebHost.UseSockets(options => options.CreateBoundListenSocket = listener.CreateSocket);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxConcurrentConnections = 64;
            options.Limits.MaxRequestBodySize = StrictJson.MaximumBytes;
            options.Limits.Http2.MaxStreamsPerConnection = 16;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            if (mcpPort is { } endpoint) options.Listen(IPAddress.Loopback, endpoint);
            options.Listen(controlStartup.AllowRemoteAccess ? IPAddress.Any : IPAddress.Loopback, port);
        });
        await using var app = builder.Build();
        await using var maintenance = new HostMaintenanceController(instance, runtime.Database, instanceLease, contracts,
            payloadRoots,
            lifecycle, resources, app.Lifetime, nativeBinding, maintenanceOrigin, ownedDesktop, services.Scheduling, windowIntent, app.Logger);
        await using var automaticUpdates = new HostAutomaticUpdates(instance, runtime.Database, settingsService,
            updates, maintenance, packages, extensions, app.Lifetime.ApplicationStopping);
        using var editShutdown = app.Lifetime.ApplicationStopping.Register(configurationEdits.PreventNewBegins);
        await using var desktop = nativeBinding is null ? null : new DesktopControl(nativeBinding, instance.InstanceId, contracts,
            lifecycle, new DesktopExitWatch(nativeBinding, lifecycle, app.Lifetime,
                ownedDesktop is null ? null : ownedDesktop.AwaitFamilyAsync), desktopSigner, desktopPresence, nativeClients,
            ownedDesktop is null ? null : settingsService.PublishDesktopMode);
        if (desktop is not null) ownedDesktop?.BindControl(desktop);
        using var bearerVerifier = new ControlBearerVerifier(instance, runtime.Database, instanceLease, runtime.Recovery, secretService);
        var controlAuthority = new ControlAuthentication(instance.InstanceId, controlStartup.AllowRemoteAccess,
            mcpPort, controlSettings.Read, bearerVerifier, TimeProvider.System);
        await using var controlTokens = new ControlTokenService(instance, runtime.Database, instanceLease, resources,
            secretService, nativeBinding, app.Lifetime.ApplicationStopping);
        app.Use(ControlExceptionBoundary.InvokeAsync);
        app.Use(new ControlRequestLimits().InvokeAsync);
        app.Use(controlAuthority.InvokeAsync);
        var availableCapabilities = new HashSet<NexusPipeline.Core.Control.ControlCapability>
            {
                NexusPipeline.Core.Control.ControlCapability.CompletionLocal,
                NexusPipeline.Core.Control.ControlCapability.MediaGrant,
                NexusPipeline.Core.Control.ControlCapability.ExtensionsWrite,
                NexusPipeline.Core.Control.ControlCapability.ExtensionsRead,
                NexusPipeline.Core.Control.ControlCapability.NativeConfigurationOpen,
                NexusPipeline.Core.Control.ControlCapability.AutomationExchange,
                NexusPipeline.Core.Control.ControlCapability.CapabilitiesRead,
                NexusPipeline.Core.Control.ControlCapability.OperationsRead,
                NexusPipeline.Core.Control.ControlCapability.StatusRead,
                NexusPipeline.Core.Control.ControlCapability.DoctorRead,
                NexusPipeline.Core.Control.ControlCapability.MaintenanceRead,
                NexusPipeline.Core.Control.ControlCapability.MaintenanceWrite,
                NexusPipeline.Core.Control.ControlCapability.AutomationRead,
                NexusPipeline.Core.Control.ControlCapability.AutomationWrite,
                NexusPipeline.Core.Control.ControlCapability.AutomationDeclare,
                NexusPipeline.Core.Control.ControlCapability.AutomationDelete,
                NexusPipeline.Core.Control.ControlCapability.SchedulingRead,
                NexusPipeline.Core.Control.ControlCapability.ConfigurationRead,
                NexusPipeline.Core.Control.ControlCapability.ConfigurationEdit,
                NexusPipeline.Core.Control.ControlCapability.ConfigurationRepair,
                NexusPipeline.Core.Control.ControlCapability.ExecutionPreview,
                NexusPipeline.Core.Control.ControlCapability.ExecutionStart,
                NexusPipeline.Core.Control.ControlCapability.ExecutionCancel,
                NexusPipeline.Core.Control.ControlCapability.RecordsRead,
                NexusPipeline.Core.Control.ControlCapability.LogsRead,
                NexusPipeline.Core.Control.ControlCapability.ImagesRead,
                NexusPipeline.Core.Control.ControlCapability.RecordsRetire,
                NexusPipeline.Core.Control.ControlCapability.SettingsRead,
                NexusPipeline.Core.Control.ControlCapability.SettingsWrite,
                NexusPipeline.Core.Control.ControlCapability.SecretStatus,
                NexusPipeline.Core.Control.ControlCapability.SecretRead,
                NexusPipeline.Core.Control.ControlCapability.SecretWrite,
                NexusPipeline.Core.Control.ControlCapability.NotificationsRead,
                NexusPipeline.Core.Control.ControlCapability.NotificationsWrite,
                NexusPipeline.Core.Control.ControlCapability.NotificationsDelete,
                NexusPipeline.Core.Control.ControlCapability.NotificationsTest
            };
        if (desktop is not null) availableCapabilities.Add(NexusPipeline.Core.Control.ControlCapability.Desktop);
        new ControlQueries(controlAuthority, operations, instance.InstanceId, () => desktop?.VerificationKeyBase64, availableCapabilities).Map(app);
        new LocalTokenControl(controlAuthority, controlTokens).Map(app);
        new HostSecretsControl(controlAuthority, secretService, instance.InstanceId).Map(app);
        var notificationUseCases = new NexusPipeline.Host.Control.Notifications.NotificationUseCases(instance.InstanceId,
            new NexusPipeline.Platform.Storage.Notifications.NotificationCatalogService(instance, runtime.Database,
                instanceLease, resources, runtime.Recovery, contracts), services.Notifications, secretService);
        new NotificationControl(controlAuthority, notificationUseCases, contracts).Map(app);
        new ServiceSettingsControl(controlAuthority, settingsService, contracts).Map(app);
        new HostSettingsControl(controlAuthority, new NexusPipeline.Host.Control.Settings.SettingsUseCases(settingsService), contracts).Map(app);
        var interfacePreferences = new InterfacePreferences(instanceLease, resources);
        new InterfaceControl(controlAuthority, interfacePreferences, contracts).Map(app);
        var exchange = new NexusPipeline.Host.Control.Automation.ExchangeUseCases(definitions, automationImports, new AutomationBundleCodec(contracts));
        new AutomationExchangeControl(controlAuthority, exchange, contracts).Map(app);
        new ExtensionControl(controlAuthority, new NexusPipeline.Host.Control.Extensions.ExtensionUseCases(packages, extensions,
            extensionOperations, app.Lifetime.ApplicationStopping), contracts).Map(app);
        new ExtensionDetailsControl(controlAuthority, new ExtensionDetailsReader(instance, runtime.Database, instanceLease, contracts, payloadRoots)).Map(app);
        var extensionFrontend = new ExtensionFrontendControl(controlAuthority, extensions.Activator); extensionFrontend.Map(app);
        if (desktop is not null) new NativeFrontendControl(controlAuthority, extensionFrontend.Clients, desktop, nativeClients).Map(app);
        if (desktop is not null) new DesktopLoginControl(controlAuthority, desktop, browserLogins, contracts).Map(app);
        new NativeConfigurationControl(controlAuthority, new NativeEditorService(instance, runtime.Database, contracts,
            instanceLease, resources, configurationEdits), configurationEdits, contracts).Map(app);
        new ConfigurationImportControl(controlAuthority, configurationImports).Map(app);
        await using var mediaControl = new NexusPipeline.Host.Control.Media.MediaUseCases(instance.InstanceId, services.LiveMedia);
        new MediaControl(controlAuthority, mediaControl, contracts).Map(app);
        var doctor = new NexusPipeline.Platform.Storage.Sqlite.RuntimeDoctor(instance, runtime.Database, runtime.Recovery);
        var runtimeUseCases = new NexusPipeline.Host.Control.Runtime.RuntimeUseCases(doctor, services.Consumer, services.Notifications,
            automaticUpdates, services.Scheduling, new(payloadRoots.HostPayloadRoot, port, mcpPort, definitions.Read, extensions));
        var recordUseCases = new NexusPipeline.Host.Control.Records.RecordUseCases(
            new NexusPipeline.Platform.Storage.Sqlite.RunRecordStore(runtime.Database),
            new NexusPipeline.Platform.Storage.Sqlite.AttemptHistoryReader(runtime.Database),
            new LogCatalogReader(instance, runtime.Database, contracts),
            new ExecutionLogReader(instance, runtime.Database, contracts),
            new ImageCatalogReader(instance, runtime.Database, contracts),
            new NexusPipeline.Platform.Storage.Records.ContentRetirementService(instance, runtime.Database,
                contracts, instanceLease, resources, runtime.Recovery));
        var eventStatistics = new NexusPipeline.Host.Control.Events.ControlEventStatistics(
            () => runtime.Database.CommitSequence, () =>
            {
                var counts = doctor.ReadCounts();
                return new(definitions.Read().Revision, controlSettings.Read().Revision,
                    counts.PendingOperations, counts.Attempts, counts.CompletedRuns);
            });
        var eventFeed = new NexusPipeline.Host.Control.Events.ControlEventFeed(instance.InstanceId,
            () => new(runtime.Database.CommitSequence, runtime.Recovery.Current.Revision, execution.ProgressRevision,
                interfacePreferences.ChangeVersion, ExtensionFrontendControl.Identity(extensions.Activator)), () =>
        {
            var statistics = eventStatistics.Read();
            var recovery = runtime.Recovery.Current;
            return new { mutationSequence = runtime.Database.CommitSequence, executionRevision = execution.ProgressRevision,
                catalogRevision = statistics.CatalogRevision, settingsRevision = statistics.SettingsRevision,
                interfaceRevision = interfacePreferences.PublishedRevision,
                extensionGeneration = ExtensionFrontendControl.Identity(extensions.Activator),
                admission = recovery.IsReady && recovery.Isolations.Count == 0 && recovery.ResourceHolds.Count == 0 ? "Ready" : "Blocked",
                recoveryRevision = recovery.Revision, pendingOperations = statistics.PendingOperations,
                attempts = statistics.Attempts, completedRuns = statistics.CompletedRuns };
        });
        new ControlEvents(controlAuthority, eventFeed).Map(app);
        new ExecutionReadControl(controlAuthority, execution, new LiveLogReader(instance, instanceLease)).Map(app);
        WebAssets.Map(app);
        new RuntimeQueries(controlAuthority, runtimeUseCases).Map(app);
        new MaintenanceControl(controlAuthority, maintenance).Map(app);
        new HostUpdateControl(controlAuthority, updates, contracts).Map(app);
        new ExecutionControl(controlAuthority, executionControlUseCases, contracts).Map(app);
        new AutomationControl(controlAuthority, automationControl, new AutomationControlCodec(contracts),
            new NexusPipeline.Host.Control.Automation.AccountSummaries(definitions,
                new NexusPipeline.Platform.Storage.Sqlite.RecordSummaryReader(runtime.Database), execution, TimeProvider.System)).Map(app);
        new EmulatorControl(controlAuthority, emulators, contracts).Map(app);
        new ConfigurationValueControl(controlAuthority, configurationValues, contracts).Map(app);
        new ConfigurationReadControl(controlAuthority, new ConfigurationOverviewReader(instance, runtime.Database, instanceLease, contracts, providers)).Map(app);
        new AdapterControl(controlAuthority, new NexusPipeline.Host.Control.Configuration.AdapterUseCases(taskAdapters, contracts), contracts).Map(app);
        var scriptProjects = new ScriptProjectService(instance, runtime.Database, instanceLease, resources, contracts, payloadRoots, extensions.Activator);
        var scriptSources = new NexusPipeline.Host.Control.Configuration.SourceUseCases(
            new ScriptSourceService(instance, runtime.Database, instanceLease, resources, contracts, payloadRoots, extensions.Activator), contracts);
        new ScriptSourceControl(controlAuthority, scriptSources).Map(app);
        var projectUseCases = new NexusPipeline.Host.Control.Configuration.ProjectUseCases(projectCompilation, projectPreviews, contracts, scriptProjects);
        new ProjectControl(controlAuthority, projectUseCases, contracts).Map(app);
        new ProjectFrontendControl(instance.InstanceId, controlAuthority, extensionFrontend, projectUseCases, contracts).Map(app);
        new RecordControl(controlAuthority, recordUseCases, contracts).Map(app);
        new RecordSummaryControl(controlAuthority, new NexusPipeline.Platform.Storage.Sqlite.RecordSummaryReader(runtime.Database),
            new NexusPipeline.Platform.Storage.Records.RecordDisplayReader(instance, runtime.Database, contracts)).Map(app);
        new ExecutionReportControl(controlAuthority, new ExecutionLogReader(instance, runtime.Database, contracts)).Map(app);
        new NexusPipeline.Host.Control.Mcp.McpControl(controlAuthority,
            new NexusPipeline.Host.Control.Mcp.McpControlTools(
                new NexusPipeline.Host.Control.Mcp.McpNotificationTools(notificationUseCases),
                new NexusPipeline.Host.Control.Mcp.McpRecordTools(recordUseCases, runtimeUseCases),
                new NexusPipeline.Host.Control.Mcp.McpExecutionTools(executionControlUseCases, contracts),
                new NexusPipeline.Host.Control.Mcp.McpAutomationTools(automationControl, emulators, contracts, scriptSources),
                new NexusPipeline.Host.Control.Mcp.McpConfigurationTools(configurationValues, configurationImports),
                new NexusPipeline.Host.Control.Mcp.McpProjectTools(projectUseCases),
                new NexusPipeline.Host.Control.Mcp.McpOperationTools(operations, availableCapabilities))).Map(app);
        desktop?.Map(app);
        secrets.Map(app);
        new CompletionLocalControl(services.Authorization, services.Admission, services.Completion, controlAuthority).Map(app);
        try
        {
            var bundle = installationBundle ?? Path.Combine(payloadRoots.HostPayloadRoot, "plugins");
            if (installationBundle is not null || Directory.Exists(bundle))
                await packages.InstallBundledAsync(bundle, allPackages: installationBundle is not null);
            if (installationBundle is not null) return 0;
            await extensions.StartAsync(CancellationToken.None);
            await app.StartAsync();
            ownedDesktop?.Start();
            var startupUpdating = await automaticUpdates.ApplyStartupAsync();
            if (!startupUpdating) { services.Start(app.Lifetime); automaticUpdates.Start(); }
            var webAddress = app.Urls.Single(value => new Uri(value).Port != mcpPort);
            var advertised = new UriBuilder(webAddress) { Host = IPAddress.Loopback.ToString() }.Uri.GetLeftPart(UriPartial.Authority);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "host.ready", instanceId = instance.InstanceId,
                address = advertised, processId = Environment.ProcessId,
                desktopVerificationKey = desktop?.VerificationKeyBase64
            }));
            await app.WaitForShutdownAsync();
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                try { await app.StopAsync(deadline.Token); }
                finally { await packages.StopAsync(TimeSpan.FromSeconds(10)); }
            }
            finally
            {
                try
                {
                    try { await mediaControl.DisposeAsync(); }
                    finally { try { await configurationEdits.StopAsync(TimeSpan.FromSeconds(10)); } finally { await services.StopAsync(); } }
                }
                finally
                {
                    try { await providers.StopMonitorAsync(); }
                    finally { await extensions.StopAsync(TimeSpan.FromSeconds(10)); }
                }
            }
        }
        return 0;
    }
}
