using System.Runtime.Versioning;
using System.Collections.Immutable;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal sealed class InstalledBettergiFixture : IAsyncDisposable
{
    internal const string Subject = "formal-bettergi-installed";
    internal readonly PackageBettergiInputs Inputs = new();
    internal readonly ExecutionAdmissionFixture Execution = new(saved: false, otherExtensionId: "bettergi");
    internal readonly InstallRuntimeFixture Installation;
    internal readonly TaskAdapterService Adapters;
    internal readonly ExecutionAdmissionService Service;
    internal readonly ExecutionRuntimeConsumer Consumer;
    internal PlanBuilderFixture Source => Execution.Source;
    internal readonly ConfigurationAddress Address;

    internal InstalledBettergiFixture(bool protectRepair = false, string mode = "bettergi-mail-failed", bool configurationInSource = false)
    {
        var secretPolicy = new SecretAccessPolicy(Source.Instance, Source.Database, Source.Lease, Execution.Resources);
        Installation = new(managedPayloadEdit: Inputs.Populate, catalog: Execution.CreateCatalog(),
            portDependencies: new(null, null, secretPolicy, null, null, null),
            originalManifest: Inputs.Manifest, originalManifestBytes: Inputs.ManifestBytes,
            payloadRoots: new(AppContext.BaseDirectory, Path.Combine(Source.Instance.RuntimeRoot, "extensions")));
        Adapters = new(Source.Instance, Source.Database, Source.Lease, Execution.Resources, new(),
            Installation.Roots, Installation.Activator, secretPolicy);
        var definitions = Source.Definitions.Read();
        var script = definitions.Scripts.Single(item => item.Id == Source.OtherScriptId);
        var native = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Formal controlled executable", AutomationRootKind.Script,
            configurationInSource ? Path.GetDirectoryName(Source.Configuration)! : Path.GetDirectoryName(native)!);
        if (configurationInSource) File.Copy(native, Path.Combine(root.Path, Path.GetFileName(native)));
        var logDirectory = Path.Combine(Source.Root, "bettergi-logs"); Directory.CreateDirectory(logDirectory);
        var logs = new AutomationRoot(Guid.NewGuid(), 0, "Installed adapter controlled logs", AutomationRootKind.External, logDirectory);
        var completion = new ConfigurationSelector([new ConfigurationProperty("CompletionAction")]);
        var tasks = new ConfigurationSelector([new ConfigurationProperty("TaskEnabledList")]);
        var resource = new AutomationAdapterResource("one-dragon", 0, "", 1, ConfigurationSyntax.Json,
            new([new(completion, false), new(tasks, true)], [new(completion, false), new(tasks, true)],
                protectRepair ? [completion] : [new([new ConfigurationProperty("TaskDefinitions")])]));
        var changes = new AutomationChanges(definitions.Revision, [root, logs], [script with
        {
            RootId = root.Id, Executable = new(root.Id, Path.GetFileName(native)),
            Arguments = ["process-output", mode, Source.Configuration, Path.Combine(logDirectory, "events.log")],
            ConfigurationPaths = definitions.Scripts.Single(item => item.Id == Source.ScriptId).ConfigurationPaths
                .Select(path => configurationInSource ? path with { RootId = root.Id } : path).ToImmutableArray(),
            AdapterResources = [resource], LogPaths = [new(logs.Id, "events.log")], Limits = new(1, 120, 120),
            Judgement = new(AutomationJudgementLanguage.Keywords, [], [], null), UpdateConfigurationAtFinish = true
        }], [], [], [], [], []);
        Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), Subject, changes), changes, DateTimeOffset.UtcNow);
        definitions = Source.Definitions.Read(); script = definitions.Scripts.Single(item => item.Id == Source.OtherScriptId);
        resource = script.AdapterResources.Single();
        var binding = definitions.Bindings.Single(item => item.Id == Source.OtherBinding);
        File.WriteAllText(Source.Configuration, "{\"TaskEnabledList\":{\"mail\":true},\"TaskDefinitions\":{\"mail\":\"领取邮件\"},\"CompletionAction\":\"Exit\"}");
        var tree = new FileTreeArchive(new(SnapshotBudget.Default), new(Source.Instance, new())).Capture(Source.Configuration, default).Snapshot;
        Address = new(Source.OtherBinding, resource.ResourceId);
        var saved = new ConfigurationRevisionChange(Address, 0, binding.Revision, AdapterConfigurationPolicy.Contract(script, resource), tree, ConfigurationUpdateKind.Manual);
        new ConfigurationRevisionStore(Source.Instance, Source.Database, new()).Commit(
            ConfigurationRevisionStore.Identity(Guid.NewGuid(), Subject, saved), saved, DateTimeOffset.UtcNow);
        Service = new(Source.Instance, Source.Database, new(), Source.Lease, Execution.Resources, Execution.Index,
            Execution.Inbox, new(15, 15, 15), payloadRoots: Installation.Roots);
        Service.ConfigureTaskAdapters(Adapters);
        Consumer = new(Source.Instance, Source.Database, new(), Source.Lease, Execution.Resources, Execution.Index, Execution.Inbox);
        Consumer.ConfigureTaskAdapters(Adapters);
    }

    internal async Task InstallAsync()
    {
        _ = await Installation.Session().ApplyAsync(Installation.Prepared, default);
        Installation.Prepared.Dispose(); Installation.Verified.Dispose(); Installation.Root.Dispose();
    }

    internal AdapterSelection Selection()
    {
        var binding = Source.Definitions.Read().Bindings.Single(item => item.Id == Source.OtherBinding);
        var installed = new ExtensionInstalledEvidence(Source.Instance, Source.Database, new(), Installation.Roots).ReadCurrent("bettergi")!;
        return new(Guid.NewGuid(), binding.Id, binding.Revision, "bettergi", installed.Metadata.Revision,
            [new("one-dragon", new ConfigurationRevisionStore(Source.Instance, Source.Database, new()).Read(Address)!.Revision, "")]);
    }

    internal AttemptAcceptanceResult Start(Guid operation) => Service.Start(operation, Subject,
        Source.Request(new BindingTarget(Source.OtherBinding)), DateTimeOffset.UtcNow);

    public async ValueTask DisposeAsync()
    {
        try { await Installation.Activator.StopAllAsync(TimeSpan.FromSeconds(15)); }
        finally { Installation.Dispose(); Execution.Dispose(); Inputs.Dispose(); }
    }
}
