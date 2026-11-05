using System.Diagnostics;
using System.Runtime.Versioning;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Settings;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Execution;
using Xunit;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class ProviderRuntimeFixture : IAsyncDisposable
{
    internal ExecutionAdmissionFixture Base { get; } = new(otherExtensionId: "provider-fixture");
    internal ProviderPackageFixture Package { get; }
    internal ExtensionScope Scope { get; } = new("provider-fixture");
    internal ExecutionProviderRegistry Registry { get; } = new();
    internal PlanBuilderFixture Source => Base.Source;
    internal string Marker { get; }
    internal string LauncherMarker => Marker + ".launcher";
    internal string LauncherRelease => Marker + ".launcher-release";
    internal ConfigurationAddress? Address { get; }
    internal ExecutionAdmissionService Service { get; }
    internal ExecutionRuntimeConsumer Consumer { get; }
    internal AttemptAcceptanceResult Accepted { get; private set; } = null!;
    internal AttemptWriterState State => Source.Lease.PublishExecution(() =>
        AttemptWriterState.Read(Source.Instance, Source.Database, new(), Accepted.JournalId));
    private bool _finished;

    private ProviderRuntimeFixture(string mode, bool configuration, bool invalidContract, bool launcher, bool invalidLauncher, bool launcherChild, bool retrySafe, bool retryApproved, int maximumAttempts, bool saveConfigurations)
    {
        Marker = Path.Combine(Source.Root, "provider-actual-ready");
        Package = new(["provider-fixture-worker", mode, Marker], configuration ?
            [new ExecutionProviderResource { ResourceId = "provider-main", DeclarationIndex = 0, DefinitionRevision = 1 }] : [],
            launcher ? [LauncherMarker, Guid.NewGuid().ToString("D"), configuration ? Source.Configuration : "-", LauncherRelease] : null, invalidLauncher, launcherChild, retrySafe,
            gracefulStop: mode == "graceful-stop" || mode.StartsWith("device-", StringComparison.Ordinal),
            targetControl: mode.StartsWith("device-", StringComparison.Ordinal));
        var original = JudgementInterpreterLease.Inspect(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!);
        foreach (var runtime in original.RuntimeFiles)
        {
            var destination = Path.Combine(Path.GetDirectoryName(Source.OtherImage)!, runtime.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(Path.GetDirectoryName(original.Path)!, runtime.RelativePath), destination, overwrite: true);
        }
        var catalog = Source.Definitions.Read();
        var script = catalog.Scripts.Single(script => script.Id == Source.OtherScriptId);
        var changes = new AutomationChanges(catalog.Revision, [], [script with
        {
            Kind = AutomationKind.Specialized, ExtensionId = "provider-fixture",
            ConfigurationPaths = configuration ? catalog.Scripts.Single(item => item.Id == Source.ScriptId).ConfigurationPaths : [], LogPaths = [],
            Judgement = new(AutomationJudgementLanguage.Keywords, [], [], null), UpdateConfigurationAtFinish = true,
            Limits = new(maximumAttempts, 120, 120),
            Target = mode.StartsWith("device-", StringComparison.Ordinal) ? new(AutomationTargetKind.Pc, true, 10, true,
                new(script.Executable, ["capture-window-fixture", Marker + ".window", mode == "device-screenDc" ? "screen-frame" : "static-frame"], script.Executable, AutomationPcReadiness.VisibleWindow), null) : script.Target
        }], [], [], [], [], []);
        Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "provider-runtime-fixture", changes), changes, DateTimeOffset.UtcNow);
        if (configuration && saveConfigurations)
        {
            Address = new(Source.OtherBinding, "provider-main");
            var binding = Source.Definitions.Read().Bindings.Single(item => item.Id == Source.OtherBinding);
            var path = Source.Definitions.Read().Scripts.Single(item => item.Id == Source.OtherScriptId).ConfigurationPaths.Single();
            File.WriteAllText(Source.Configuration, "{\"fixture\":false}");
            var originalConfiguration = File.ReadAllBytes(Source.Configuration);
            try
            {
                File.WriteAllText(Source.Configuration, "{\"fixture\":true}");
                var archive = new FileTreeArchive(new(SnapshotBudget.Default), new(Source.Instance, new()));
                var tree = archive.Capture(Source.Configuration, default).Snapshot;
                var contract = invalidContract ? GenericConfigurationPolicy.Contract(path) :
                    ProviderConfigurationPolicy.Contract("provider-fixture", "main", new("provider-main", 0, 1));
                var change = new ConfigurationRevisionChange(Address, 0, binding.Revision, contract, tree, ConfigurationUpdateKind.Manual);
                new ConfigurationRevisionStore(Source.Instance, Source.Database, new()).Commit(
                    ConfigurationRevisionStore.Identity(Guid.NewGuid(), "provider-runtime-fixture", change), change, DateTimeOffset.UtcNow);
            }
            finally { File.WriteAllBytes(Source.Configuration, originalConfiguration); }
        }
        if (retryApproved)
        {
            var beforeSettings = Source.Settings.Read();
            var change = new HostSettingsChange(beforeSettings.Revision, beforeSettings.Values with { AutomaticRetry = new(true, 0) });
            Source.Settings.Commit(HostSettingsStore.Identity(Guid.NewGuid(), "provider-retry-fixture", change), change, DateTimeOffset.UtcNow);
        }
        Service = new(Source.Instance, Source.Database, new(), Source.Lease, Base.Resources, Base.Index, Base.Inbox, new(15, 15, 15),
            null, null, mode.StartsWith("device-", StringComparison.Ordinal) ? new(Environment.GetEnvironmentVariable("NEXUS_TEST_HOST_EXE")!, null) : null, Registry);
        Consumer = new(Source.Instance, Source.Database, new(), Source.Lease, Base.Resources, Base.Index, Base.Inbox,
            liveMedia: new ExecutionLiveMedia(Source.Instance.InstanceId));
    }

    internal static async Task<ProviderRuntimeFixture> PrepareAsync(string mode, bool configuration = false, bool invalidContract = false,
        bool launcher = false, bool invalidLauncher = false, bool launcherChild = false,
        bool retrySafe = false, bool retryApproved = false, int maximumAttempts = 1, bool saveConfigurations = true)
    {
        var fixture = new ProviderRuntimeFixture(mode, configuration, invalidContract, launcher, invalidLauncher, launcherChild,
            retrySafe, retryApproved, maximumAttempts, saveConfigurations);
        try
        {
            await new ExecutionRegistrationPort(fixture.Scope, fixture.Package.Verify(), fixture.Registry).RegisterAsync(default);
            fixture.Scope.Publish();
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    internal static async Task<ProviderRuntimeFixture> CreateAsync(string mode, bool configuration = false, bool launcher = false, bool invalidLauncher = false, bool launcherChild = false,
        bool retrySafe = false, bool retryApproved = false, int maximumAttempts = 1)
    {
        var fixture = await PrepareAsync(mode, configuration, launcher: launcher, invalidLauncher: invalidLauncher, launcherChild: launcherChild,
            retrySafe: retrySafe, retryApproved: retryApproved, maximumAttempts: maximumAttempts);
        try
        {
            fixture.Start();
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    internal void Start(PreparedMuMuDiscovery? discovery = null) => Accepted = discovery is null
        ? Service.Start(Guid.NewGuid(), "provider-runtime-fixture", Source.Request(new BindingTarget(Source.OtherBinding)), DateTimeOffset.UtcNow)
        : Service.StartWithDiscoveries(Guid.NewGuid(), "provider-runtime-fixture", Source.Request(new BindingTarget(Source.OtherBinding)),
            DateTimeOffset.UtcNow, new Dictionary<Guid, PreparedMuMuDiscovery> { [Source.OtherScriptId] = discovery });

    internal async Task ReadyAsync()
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(Marker) && timer.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20);
        Assert.True(File.Exists(Marker), Marker);
    }

    internal void Cancel()
    {
        var state = State;
        new ExecutionCancellationStore(Source.Instance, Source.Database, new(), Source.Lease).Cancel(
            ExecutionCancellationStore.Identity(Guid.NewGuid(), "provider-runtime-fixture", Accepted.JournalId, state.Journal.OperationId,
                Guid.Parse(state.Attempt.RunId), Guid.Parse(state.Attempt.AttemptId)), Accepted.JournalId, DateTimeOffset.UtcNow);
    }

    internal async Task<AttemptReceipt> RunAsync()
    {
        var receipt = Assert.IsType<AttemptReceipt>(await Consumer.RunNextAsync());
        _finished = true; return receipt;
    }

    internal async Task<AttemptReceipt> RunInterruptedAsync()
    {
        using var stopping = new CancellationTokenSource();
        var running = Consumer.RunNextAsync(stopping.Token);
        try
        {
            var marker = Marker + (Address is null ? ".attempts" : ".configuration-1");
            var timer = Stopwatch.StartNew();
            while (!File.Exists(marker) && !running.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(15))
                await Task.Delay(20);
            Assert.True(File.Exists(marker), "interrupted_worker_not_started");
        }
        finally { stopping.Cancel(); }
        var receipt = Assert.IsType<AttemptReceipt>(await running); _finished = true; return receipt;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_finished && Accepted is not null && Base.Inbox.PendingCount > 0)
        {
            Cancel(); _ = await RunAsync();
        }
        if (Package.Launcher is not null)
        {
            File.WriteAllText(LauncherRelease, "release");
            var timer = Stopwatch.StartNew();
            while (Registry.RetainedCount != 0 && timer.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20);
            Assert.Equal(0, Registry.RetainedCount);
        }
        await Registry.StopMonitorAsync();
        await Scope.DisposeAsync(); Base.Dispose(); Package.Dispose();
    }
}
