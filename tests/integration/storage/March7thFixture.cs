using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text;
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
internal sealed class March7thFixture : IAsyncDisposable
{
    internal const string Subject = "formal-march7th-installed";
    internal static readonly string[] TaskKeys = ["daily_enable", "power_enable", "universe_enable", "weekly_divergent_enable",
        "currencywars_enable", "fight_enable", "forgottenhall_enable", "purefiction_enable", "apocalyptic_enable",
        "asset_manager_enable", "reward_enable", "activity_enable", "build_target_enable", "echo_of_war_enable",
        "reward_assist_enable", "reward_mail_enable", "reward_dispatch_enable", "reward_quest_enable", "reward_srpass_enable",
        "reward_achievement_enable", "reward_message_enable", "reward_redemption_code_enable", "asset_self_molding_resin_enable",
        "asset_lc3_star_superimpose_enable", "asset_ember_special_pass_enable", "asset_ember_regular_pass_enable",
        "asset_ember_tracks_of_destiny_enable", "activity_dailycheckin_enable", "activity_gardenofplenty_enable",
        "activity_realmofthestrange_enable", "activity_planarfissure_enable", "activity_journey_highlights_notification_enable"];
    internal readonly SpecializedPackageInputs Inputs = new("march7th", "NEXUS_FORMAL_MARCH7TH_PACKAGE_RECEIPT");
    internal readonly ExecutionAdmissionFixture Execution = new(saved: false, otherExtensionId: "march7th");
    internal readonly InstallRuntimeFixture Installation;
    internal readonly TaskAdapterService Adapters;
    internal readonly ExecutionAdmissionService Service;
    internal readonly ExecutionRuntimeConsumer Consumer;
    internal readonly ConfigurationAddress Address;
    internal readonly string Configuration, Log;
    internal readonly byte[] Original;
    internal PlanBuilderFixture Source => Execution.Source;

    internal March7thFixture(bool child)
    {
        var secrets = new SecretAccessPolicy(Source.Instance, Source.Database, Source.Lease, Execution.Resources);
        Installation = new(catalog: Execution.CreateCatalog(), originalManifest: Inputs.Manifest,
            originalManifestBytes: Inputs.ManifestBytes, packageArchive: Inputs.Archive,
            portDependencies: new(null, null, secrets, null, null, null),
            payloadRoots: new(AppContext.BaseDirectory, Path.Combine(Source.Instance.RuntimeRoot, "extensions")));
        Adapters = new(Source.Instance, Source.Database, Source.Lease, Execution.Resources, new(),
            Installation.Roots, Installation.Activator, secrets);
        var definitions = Source.Definitions.Read();
        var script = definitions.Scripts.Single(item => item.Id == Source.OtherScriptId);
        var native = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!;
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Controlled March7th output", AutomationRootKind.Script, Path.GetDirectoryName(native)!);
        var directory = Path.Combine(Source.Root, "march7th-input"); Directory.CreateDirectory(directory);
        var files = new AutomationRoot(Guid.NewGuid(), 0, "Isolated March7th configuration and logs", AutomationRootKind.External, directory);
        Configuration = Path.Combine(directory, "config.yaml"); Log = Path.Combine(directory, "events.log");
        var selectors = TaskKeys.Select(key => new ConfigurationAccessGrant(new([new ConfigurationProperty(key)]), false)).ToImmutableArray();
        var access = new ConfigurationDocumentAccess(selectors, selectors, [new([new ConfigurationProperty("last_run_timestamp")])]);
        var resource = new AutomationAdapterResource("main", 0, "", 1, ConfigurationSyntax.Yaml, access);
        var changes = new AutomationChanges(definitions.Revision, [root, files], [script with
        {
            RootId = root.Id, Executable = new(root.Id, Path.GetFileName(native)),
            Arguments = ["process-output", child ? "march7th-child" : "march7th-roots", Configuration, Log],
            ConfigurationPaths = [new(files.Id, "config.yaml")], AdapterResources = [resource], LogPaths = [new(files.Id, "events.log")],
            Limits = new(2, 120, 120), Judgement = new(AutomationJudgementLanguage.Keywords, [], [], null),
            UpdateConfigurationAtFinish = true
        }], [], [], [], [], []);
        Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), Subject, changes), changes, DateTimeOffset.UtcNow);
        var enabled = child ? new[] { "daily_enable", "reward_enable", "reward_mail_enable" } : ["daily_enable", "forgottenhall_enable"];
        var text = string.Join("\n", TaskKeys.Select(key => key + ": " + (enabled.Contains(key) ? "true" : "false"))) +
            "\ngame_path: controlled.exe\nafter_finish: None\nlast_run_timestamp: preserved\nuniverse_count: 7\nfuture_enable: false\n";
        Original = new UTF8Encoding(false).GetBytes(text); File.WriteAllBytes(Configuration, Original);
        definitions = Source.Definitions.Read(); script = definitions.Scripts.Single(item => item.Id == Source.OtherScriptId);
        resource = script.AdapterResources.Single();
        var binding = definitions.Bindings.Single(item => item.Id == Source.OtherBinding);
        var tree = new FileTreeArchive(new(SnapshotBudget.Default), new(Source.Instance, new())).Capture(Configuration, default).Snapshot;
        Address = new(binding.Id, resource.ResourceId);
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
        var installed = new ExtensionInstalledEvidence(Source.Instance, Source.Database, new(), Installation.Roots).ReadCurrent("march7th")!;
        return new(Guid.NewGuid(), binding.Id, binding.Revision, "march7th", installed.Metadata.Revision,
            [new("main", new ConfigurationRevisionStore(Source.Instance, Source.Database, new()).Read(Address)!.Revision, "")]);
    }

    public async ValueTask DisposeAsync()
    {
        try { await Installation.Activator.StopAllAsync(TimeSpan.FromSeconds(15)); }
        finally { Installation.Dispose(); Execution.Dispose(); Inputs.Dispose(); }
    }
}
