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
internal sealed class OkNteFixture : IAsyncDisposable
{
    internal readonly SpecializedPackageInputs Inputs = new("oknte", "NEXUS_FORMAL_OKNTE_PACKAGE_RECEIPT");
    internal readonly ExecutionAdmissionFixture Execution = new(saved: false, otherExtensionId: "oknte");
    internal readonly InstallRuntimeFixture Installation;
    internal readonly ExecutionAdmissionService Service;
    internal readonly ExecutionRuntimeConsumer Consumer;
    internal readonly string Configuration, Log;
    internal readonly byte[] Original;
    internal PlanBuilderFixture Source => Execution.Source;

    internal OkNteFixture()
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
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Controlled OkNTE process", AutomationRootKind.Script, Path.GetDirectoryName(native)!);
        var directory = Path.Combine(Source.Root, "oknte-input"); Directory.CreateDirectory(directory);
        var files = new AutomationRoot(Guid.NewGuid(), 0, "Isolated OkNTE configuration", AutomationRootKind.External, directory);
        Configuration = Path.Combine(directory, "config.json"); Log = Path.Combine(directory, "events.log");
        string[] ids = ["daily_anomaly", "daily_anomaly_hunter", "coffee", "daily_claim", "cinema_date", "fountain", "furniture", "gift"];
        var selectors = ids.Select(id => new ConfigurationAccessGrant(new([
            new ConfigurationProperty("Routine Items"), new ConfigurationMatch("id", id),
            new ConfigurationProperty("enabled")]), false)).ToImmutableArray();
        var resource = new AutomationAdapterResource("main", 0, "", 1, ConfigurationSyntax.Json, new(selectors, selectors, []));
        var changes = new AutomationChanges(definitions.Revision, [root, files], [script with
        {
            RootId = root.Id, Executable = new(root.Id, Path.GetFileName(native)),
            Arguments = ["process-output", "oknte-restart", Configuration, Log],
            ConfigurationPaths = [new(files.Id, "config.json")], AdapterResources = [resource], LogPaths = [new(files.Id, "events.log")],
            Limits = new(2, 2, 120), Judgement = new(AutomationJudgementLanguage.Keywords, [], ["OKNTE_TEST_FAILURE"], null),
            UpdateConfigurationAtFinish = true
        }], [], [], [], [], []);
        Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "oknte-restart", changes), changes, DateTimeOffset.UtcNow);
        Original = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["Routine Items"] = ids.Select(id => new { id, enabled = id is "coffee" or "daily_claim" }).ToArray(),
            ["opaque"] = new { retained = true }
        });
        File.WriteAllBytes(Configuration, Original);
        definitions = Source.Definitions.Read(); script = definitions.Scripts.Single(item => item.Id == Source.OtherScriptId);
        resource = script.AdapterResources.Single();
        var binding = definitions.Bindings.Single(item => item.Id == Source.OtherBinding);
        var tree = new FileTreeArchive(new(SnapshotBudget.Default), new(Source.Instance, new())).Capture(Configuration, default).Snapshot;
        var saved = new ConfigurationRevisionChange(new(binding.Id, resource.ResourceId), 0, binding.Revision,
            AdapterConfigurationPolicy.Contract(script, resource), tree, ConfigurationUpdateKind.Manual);
        new ConfigurationRevisionStore(Source.Instance, Source.Database, new()).Commit(
            ConfigurationRevisionStore.Identity(Guid.NewGuid(), "oknte-restart", saved), saved, DateTimeOffset.UtcNow);
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
