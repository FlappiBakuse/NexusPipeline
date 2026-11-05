using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class BaahFixture : IAsyncDisposable
{
    internal readonly SpecializedPackageInputs Inputs = new("baah", "NEXUS_FORMAL_BAAH_PACKAGE_RECEIPT");
    internal readonly ExecutionAdmissionFixture Execution = new(saved: false, otherExtensionId: "baah");
    internal readonly InstallRuntimeFixture Installation;
    internal readonly ExecutionAdmissionService Service;
    internal readonly ExecutionRuntimeConsumer Consumer;
    internal readonly string Configuration, Log;
    internal readonly byte[] Original;
    internal PlanBuilderFixture Source => Execution.Source;

    internal BaahFixture(bool automatic)
    {
        var secrets = new SecretAccessPolicy(Source.Instance, Source.Database, Source.Lease, Execution.Resources);
        Installation = new(catalog: Execution.CreateCatalog(), originalManifest: Inputs.Manifest,
            originalManifestBytes: Inputs.ManifestBytes, packageArchive: Inputs.Archive,
            portDependencies: new(null, null, secrets, null, null, null),
            payloadRoots: new(AppContext.BaseDirectory, Path.Combine(Source.Instance.RuntimeRoot, "extensions")));
        var adapters = new TaskAdapterService(Source.Instance, Source.Database, Source.Lease, Execution.Resources, new(),
            Installation.Roots, Installation.Activator, secrets);
        var definitions = Source.Definitions.Read();
        var script = definitions.Scripts.Single(item => item.Id == Source.OtherScriptId);
        var native = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Controlled BAAH process", AutomationRootKind.Script, Path.GetDirectoryName(native)!);
        var directory = Path.Combine(Source.Root, "baah-input"); Directory.CreateDirectory(directory);
        var files = new AutomationRoot(Guid.NewGuid(), 0, "Isolated BAAH configuration", AutomationRootKind.External, directory);
        Configuration = Path.Combine(directory, "config.json"); Log = Path.Combine(directory, "events.log");
        var software = Path.Combine(directory, "software.json");
        var selectors = Enumerable.Range(0, 3).Select(index => new ConfigurationAccessGrant(new([
            new ConfigurationProperty("TASK_ORDER_GROUP"), new ConfigurationProperty("ALL_PIPELINES"), new ConfigurationIndex(0),
            new ConfigurationProperty("TASK_ONOFF"), new ConfigurationIndex(index)]), false)).Append(
                new(new([new ConfigurationProperty("OPEN_GAME_APP_TASK")]), false)).ToImmutableArray();
        var access = new ConfigurationDocumentAccess(selectors, selectors, []);
        var softwareSelectors = ImmutableArray.Create(new ConfigurationAccessGrant(new([new ConfigurationProperty("SAVE_LOG_TO_FILE")]), false));
        var resources = ImmutableArray.Create(new AutomationAdapterResource("main", 0, "", 1, ConfigurationSyntax.Json, access),
            new AutomationAdapterResource("software-config", 1, "", 1, ConfigurationSyntax.Json,
                new(softwareSelectors, softwareSelectors, [])));
        var changes = new AutomationChanges(definitions.Revision, [root, files], [script with
        {
            RootId = root.Id, Executable = new(root.Id, Path.GetFileName(native)),
            Arguments = ["process-output", "baah-restart", Configuration, Log],
            ConfigurationPaths = [new(files.Id, "config.json"), new(files.Id, "software.json")],
            AdapterResources = resources, LogPaths = [new(files.Id, "events.log")],
            Limits = new(2, 2, 120), Judgement = new(AutomationJudgementLanguage.Keywords, [], ["BAAH_TEST_FAILURE"], null),
            UpdateConfigurationAtFinish = true
        }], [], [], [], [], []);
        Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "baah-restart", changes), changes, DateTimeOffset.UtcNow);
        Original = JsonSerializer.SerializeToUtf8Bytes(new
        {
            TASK_ORDER_GROUP = new { ACTIVATE_IND = 0, ALL_PIPELINES = new[] { new { TASK_PIPELINE = new[] { "登录游戏", "咖啡馆", "邮件" }, TASK_ONOFF = new[] { true, true, true } } } },
            OPEN_GAME_APP_TASK = automatic, DO_POST_ALL_TASK = true, POST_COMMAND = "", CAFE_TOUCH = true
        });
        File.WriteAllBytes(Configuration, Original); File.WriteAllText(software, "{\"SAVE_LOG_TO_FILE\":true,\"fixture_progress\":0}");
        definitions = Source.Definitions.Read(); script = definitions.Scripts.Single(item => item.Id == Source.OtherScriptId);
        var binding = definitions.Bindings.Single(item => item.Id == Source.OtherBinding);
        foreach (var resource in script.AdapterResources)
        {
            var path = resource.ResourceId == "main" ? Configuration : software;
            var tree = new FileTreeArchive(new(SnapshotBudget.Default), new(Source.Instance, new())).Capture(path, default).Snapshot;
            var saved = new ConfigurationRevisionChange(new(binding.Id, resource.ResourceId), 0, binding.Revision,
                AdapterConfigurationPolicy.Contract(script, resource), tree, ConfigurationUpdateKind.Manual);
            new ConfigurationRevisionStore(Source.Instance, Source.Database, new()).Commit(
                ConfigurationRevisionStore.Identity(Guid.NewGuid(), "baah-restart", saved), saved, DateTimeOffset.UtcNow);
        }
        Service = new(Source.Instance, Source.Database, new(), Source.Lease, Execution.Resources, Execution.Index,
            Execution.Inbox, new(15, 15, 15), payloadRoots: Installation.Roots);
        Service.ConfigureTaskAdapters(adapters);
        Consumer = new(Source.Instance, Source.Database, new(), Source.Lease, Execution.Resources, Execution.Index, Execution.Inbox);
        Consumer.ConfigureTaskAdapters(adapters);
    }

    internal async Task InstallAsync()
    {
        _ = await Installation.Session().ApplyAsync(Installation.Prepared, default);
        Installation.Prepared.Dispose(); Installation.Verified.Dispose(); Installation.Root.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        try { await Installation.Activator.StopAllAsync(TimeSpan.FromSeconds(15)); }
        finally { Installation.Dispose(); Execution.Dispose(); Inputs.Dispose(); }
    }
}
