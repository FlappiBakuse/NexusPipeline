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
internal sealed class NativeResumeFixture : IAsyncDisposable
{
    internal readonly SpecializedPackageInputs Inputs;
    internal readonly ExecutionAdmissionFixture Execution;
    internal readonly InstallRuntimeFixture Installation;
    internal readonly ExecutionAdmissionService Service;
    internal readonly ExecutionRuntimeConsumer Consumer;
    internal readonly string Configuration, Log;
    internal readonly byte[] Original;
    internal PlanBuilderFixture Source => Execution.Source;

    internal NativeResumeFixture(string extension)
    {
        Inputs = new(extension, "NEXUS_FORMAL_" + extension.ToUpperInvariant() + "_PACKAGE_RECEIPT");
        Execution = new(saved: false, otherExtensionId: extension);
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
        var root = new AutomationRoot(Guid.NewGuid(), 0, "Controlled native continuation", AutomationRootKind.Script, Path.GetDirectoryName(native)!);
        var directory = Path.Combine(Source.Root, "native-input"); Directory.CreateDirectory(directory);
        var files = new AutomationRoot(Guid.NewGuid(), 0, "Isolated native configuration", AutomationRootKind.External, directory);
        Configuration = Path.Combine(directory, "config.json"); Log = Path.Combine(directory, "events.log");
        var changes = new AutomationChanges(definitions.Revision, [root, files], [script with
        {
            RootId = root.Id, Executable = new(root.Id, Path.GetFileName(native)),
            Arguments = ["process-output", "native-resume", extension, Configuration, Log],
            ConfigurationPaths = [new(files.Id, "config.json"), new(files.Id, "progress.json")],
            AdapterResources = [new(extension == "okww" ? "main" : "group:01", 0, "", 1, ConfigurationSyntax.Json, new([], [], [])),
                new("native-progress", 1, "", 1, ConfigurationSyntax.Json, new([], [], []))],
            LogPaths = [new(files.Id, "events.log")], Limits = new(2, 2, 120),
            Judgement = new(AutomationJudgementLanguage.Keywords, [], ["NATIVE_TEST_FAILURE"], null), UpdateConfigurationAtFinish = true
        }], [], [], [], [], []);
        Source.Definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "native-resume", changes), changes, DateTimeOffset.UtcNow);
        Original = ConfigurationBytes(extension);
        File.WriteAllBytes(Configuration, Original); File.WriteAllText(Path.Combine(directory, "progress.json"), "{\"ordinal\":0,\"remaining\":4}");
        definitions = Source.Definitions.Read(); script = definitions.Scripts.Single(item => item.Id == Source.OtherScriptId);
        var binding = definitions.Bindings.Single(item => item.Id == Source.OtherBinding);
        foreach (var resource in script.AdapterResources)
        {
            var path = resource.DeclarationIndex == 0 ? Configuration : Path.Combine(directory, "progress.json");
            var tree = new FileTreeArchive(new(SnapshotBudget.Default), new(Source.Instance, new())).Capture(path, default).Snapshot;
            var saved = new ConfigurationRevisionChange(new(binding.Id, resource.ResourceId), 0, binding.Revision,
                AdapterConfigurationPolicy.Contract(script, resource), tree, ConfigurationUpdateKind.Manual);
            new ConfigurationRevisionStore(Source.Instance, Source.Database, new()).Commit(
                ConfigurationRevisionStore.Identity(Guid.NewGuid(), "native-resume", saved), saved, DateTimeOffset.UtcNow);
        }
        Service = new(Source.Instance, Source.Database, new(), Source.Lease, Execution.Resources, Execution.Index,
            Execution.Inbox, new(15, 15, 15), payloadRoots: Installation.Roots);
        Service.ConfigureTaskAdapters(adapters);
        Consumer = new(Source.Instance, Source.Database, new(), Source.Lease, Execution.Resources, Execution.Index, Execution.Inbox);
        Consumer.ConfigureTaskAdapters(adapters);
    }

    private static byte[] ConfigurationBytes(string extension)
    {
        if (extension == "okww") return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        { ["Which to Farm"] = "Tacet Suppression", ["Farm Nightmare Nest for Daily Echo"] = false,
            ["Additional Tasks to Run After Daily Task"] = Array.Empty<string>(), ["opaque"] = "preserved" });
        string[] ids = ["redemption_code", "email", "daily_signin", "random_play", "coffee", "charge_plan", "engagement_reward", "city_fund",
            "ridu_weekly", "notorious_hunt", "lost_void", "withered_domain", "shiyu_defense", "intel_board", "suibian_temple", "world_patrol",
            "life_on_line", "drive_disc_dismantle", "notify"];
        string[] titles = ["兑换码", "邮件", "每日签到", "录像店营业", "咖啡店", "体力刷本", "活跃度奖励", "丽都城募", "丽都周纪 (领奖励)",
            "恶名狩猎", "迷失之地", "枯萎之都", "式舆防卫战", "情报板", "随便观", "锄大地", "真·拿命验收", "驱动盘拆解", "通知"];
        return JsonSerializer.SerializeToUtf8Bytes(new { app_list = ids.Select((id, index) =>
            new { app_id = id, enabled = id is "email" or "coffee" or "notify", fixture_title = titles[index] }).ToArray() });
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
