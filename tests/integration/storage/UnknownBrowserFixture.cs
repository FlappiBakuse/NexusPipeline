using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal static class UnknownBrowserFixture
{
    internal const string UnknownTitle = "Future 用户任务 Ω";

    internal static object Seed(ExtensionCatalogFixture catalog)
    {
        var worker = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new IOException("fixture_worker_required");
        var executable = new AutomationRoot(Guid.NewGuid(), 0, "Controlled BAAH executable", AutomationRootKind.Script, Path.GetDirectoryName(worker)!);
        var roots = new List<AutomationRoot> { executable };
        var scripts = new List<AutomationScript>(); var users = new List<AutomationUser>();
        var bindings = new List<AutomationBinding>(); var contexts = new List<object>();
        foreach (var enabled in new[] { true, false })
        {
            var directory = Path.Combine(Path.GetDirectoryName(catalog.Instance.RuntimeRoot)!, "baah-unknown-" + enabled);
            Directory.CreateDirectory(directory);
            var configuration = Path.Combine(directory, "config.json"); var software = Path.Combine(directory, "software.json");
            var log = Path.Combine(directory, "events.log");
            var files = new AutomationRoot(Guid.NewGuid(), 0, "Isolated BAAH configuration", AutomationRootKind.External, directory); roots.Add(files);
            var selectors = Enumerable.Range(0, 3).Select(index => new ConfigurationAccessGrant(new([
                new ConfigurationProperty("TASK_ORDER_GROUP"), new ConfigurationProperty("ALL_PIPELINES"), new ConfigurationIndex(0),
                new ConfigurationProperty("TASK_ONOFF"), new ConfigurationIndex(index)]), false)).Append(
                new(new([new ConfigurationProperty("OPEN_GAME_APP_TASK")]), false)).ToImmutableArray();
            var softwareSelectors = ImmutableArray.Create(new ConfigurationAccessGrant(new([new ConfigurationProperty("SAVE_LOG_TO_FILE")]), false));
            var script = new AutomationScript(Guid.NewGuid(), 0, "BAAH unknown " + enabled, AutomationKind.Specialized, executable.Id, "baah",
                new(executable.Id, Path.GetFileName(worker)), ["process-output", enabled ? "baah-unknown" : "baah-restart", configuration, log],
                [new(files.Id, "config.json"), new(files.Id, "software.json")], [new(files.Id, "events.log")],
                new(2, enabled ? 0 : 2, 120), new(AutomationJudgementLanguage.Keywords, ["BAAH_TEST_SUCCESS"], ["BAAH_TEST_FAILURE"], null))
            { AdapterResources = [new("main", 0, "", 1, ConfigurationSyntax.Json, new(selectors, selectors, [])),
                new("software-config", 1, "", 1, ConfigurationSyntax.Json, new(softwareSelectors, softwareSelectors, []))] };
            var user = new AutomationUser(Guid.NewGuid(), 0, "Unknown " + enabled, users.Count);
            var binding = new AutomationBinding(Guid.NewGuid(), 0, user.Id, script.Id, true, 0) { NotifyEnabled = false };
            scripts.Add(script); users.Add(user); bindings.Add(binding);
            var original = JsonSerializer.Serialize(new { TASK_ORDER_GROUP = new { ACTIVATE_IND = 0, ALL_PIPELINES = new[] {
                new { TASK_PIPELINE = new[] { "登录游戏", "咖啡馆", "邮件", UnknownTitle }, TASK_ONOFF = new[] { true, true, true, enabled },
                    opaque = new { nested = new { counter = 17, text = "用户自定义内容" } } } } },
                OPEN_GAME_APP_TASK = false, DO_POST_ALL_TASK = true, POST_COMMAND = "", CAFE_TOUCH = true });
            const string originalSoftware = "{\"SAVE_LOG_TO_FILE\":true,\"fixture_progress\":0}";
            File.WriteAllText(configuration, original); File.WriteAllText(software, originalSoftware);
            contexts.Add(new { bindingId = binding.Id, userId = user.Id, unknownEnabled = enabled, unknownTitle = UnknownTitle,
                configuration, original, software, originalSoftware, log, expectedCalls = enabled ? 1 : 2 });
        }
        var changes = new AutomationChanges(0, [.. roots], [.. scripts], [.. users], [.. bindings], [], [], []);
        var definitions = new AutomationDefinitionStore(catalog.Database);
        definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "unknown-browser", changes), changes, DateTimeOffset.UtcNow);
        var saved = definitions.Read(); var revisions = new ConfigurationRevisionStore(catalog.Instance, catalog.Database, new());
        foreach (var binding in saved.Bindings)
        {
            var script = saved.Scripts.Single(item => item.Id == binding.ScriptId);
            foreach (var resource in script.AdapterResources)
            {
                var path = script.ConfigurationPaths[resource.DeclarationIndex];
                var file = Path.Combine(saved.Roots.Single(root => root.Id == path.RootId).Path, path.RelativePath);
                var tree = new FileTreeArchive(new(SnapshotBudget.Default), new(catalog.Instance, new())).Capture(file, default).Snapshot;
                var change = new ConfigurationRevisionChange(new(binding.Id, resource.ResourceId), 0, binding.Revision,
                    AdapterConfigurationPolicy.Contract(script, resource), tree, ConfigurationUpdateKind.Manual);
                revisions.Commit(ConfigurationRevisionStore.Identity(Guid.NewGuid(), "unknown-browser", change), change, DateTimeOffset.UtcNow);
            }
        }
        return new { cases = contexts };
    }
}
