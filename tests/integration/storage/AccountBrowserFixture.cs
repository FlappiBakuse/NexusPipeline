using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;

namespace NexusPipeline.StorageTests;

[SupportedOSPlatform("windows")]
internal static class AccountBrowserFixture
{
    internal static BoundPackageFixture Package(ExtensionCatalogFixture catalog) => new("data-specialized", catalog: catalog,
        manifestEdit: manifest =>
        {
            manifest["capabilities"] = new JsonArray("task.adapt");
            manifest["permissions"] = new JsonObject { ["fileAccess"] = new JsonArray("selected-script-root") };
        }, payloadEdit: directory => File.WriteAllText(Path.Combine(directory, "adapter/main.js"), Adapter));

    internal static object Seed(ExtensionCatalogFixture catalog)
    {
        var directory = Path.Combine(Path.GetDirectoryName(catalog.Instance.RuntimeRoot)!, "account-summary-source");
        Directory.CreateDirectory(directory);
        var configuration = Path.Combine(directory, "account.json"); File.WriteAllText(configuration, "{\"enabled\":true}");
        var worker = Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE") ?? throw new IOException("fixture_worker_required");
        var imageRoot = new AutomationRoot(Guid.NewGuid(), 0, "Controlled task executable", AutomationRootKind.Script, Path.GetDirectoryName(worker)!);
        var configRoot = new AutomationRoot(Guid.NewGuid(), 0, "Controlled task configuration", AutomationRootKind.External, directory);
        var users = new[] { "U-empty", "U-success", "U-running", "U-scheduled" }
            .Select((name, index) => new AutomationUser(Guid.NewGuid(), 0, name, index)).ToArray();
        var scripts = new[] { "failed", "succeeded", "running", "succeeded" }.Select((outcome, index) =>
            new AutomationScript(Guid.NewGuid(), 0, "Summary " + index, AutomationKind.Specialized, imageRoot.Id, "fixture-plugin",
                new(imageRoot.Id, Path.GetFileName(worker)), ["account-task-fixture", outcome, Path.Combine(directory, "run-" + index)],
                [new(configRoot.Id, "account.json")], [], new(1, 0, 300), new(AutomationJudgementLanguage.Keywords, [], [], null))
            { AdapterResources = [new("account", 0, "", 1, ConfigurationSyntax.Json, new([new(new([]), true)], [], []))] }).ToArray();
        var bindings = scripts.Select((script, index) => new AutomationBinding(Guid.NewGuid(), 0,
            users[index < 2 ? 1 : index].Id, script.Id, true, index) { NotifyEnabled = false }).ToArray();
        var queue = new AutomationQueue(Guid.NewGuid(), 0, "Summary future queue", [scripts[3].Id], AutomationCompletionAction.None);
        var future = DateTimeOffset.UtcNow.AddHours(2);
        var schedule = new AutomationSchedule(Guid.NewGuid(), 0, queue.Id, true, "UTC", [(int)future.DayOfWeek], future.Hour * 60 + future.Minute);
        var changes = new AutomationChanges(0, [imageRoot, configRoot], [.. scripts], [.. users], [.. bindings], [queue], [schedule], []);
        new AutomationDefinitionStore(catalog.Database).Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "account-browser-fixture", changes), changes, DateTimeOffset.UtcNow);
        var archive = new FileTreeArchive(new(SnapshotBudget.Default), new(catalog.Instance, new()));
        var tree = archive.Capture(configuration, default).Snapshot;
        var revisions = new ConfigurationRevisionStore(catalog.Instance, catalog.Database, new());
        foreach (var binding in bindings)
        {
            var script = scripts.Single(item => item.Id == binding.ScriptId);
            var change = new ConfigurationRevisionChange(new(binding.Id, "account"), 0, 1,
                AdapterConfigurationPolicy.Contract(script, script.AdapterResources[0]), tree, ConfigurationUpdateKind.Manual);
            revisions.Commit(ConfigurationRevisionStore.Identity(Guid.NewGuid(), "account-browser-fixture", change), change, DateTimeOffset.UtcNow);
        }
        return new { queueId = queue.Id, users = users.Select(user => new { userId = user.Id, name = user.Name }),
            bindings = bindings.Select(binding => binding.Id), scheduledAt = future.ToString("O") };
    }

    private const string Adapter = """
        globalThis.nexusTaskAdapter = input => {
          const result = { format: input.format, version: input.version, direction: 'response', phase: input.phase,
            extensionId: input.extensionId, operationId: input.operationId };
          if (input.phase === 'observe' || input.phase === 'retry') {
            result.runId = input.runId; result.attemptId = input.attemptId; result.planDigest = input.planDigest;
          }
          if (input.phase === 'observe') {
            const line = (input.logEvents ?? []).find(event => event.text.startsWith('account-task:'));
            const state = line?.text.trim().slice('account-task:'.length) ?? 'unknown';
            result.observations = input.tasks.map(task => ({ taskId: task.taskId,
              state: ['failed', 'succeeded', 'running'].includes(state) ? state : 'unknown', reasonCode: 'controlled_account_task' }));
          } else if (input.phase === 'retry') {
            result.idempotencyKey = input.idempotencyKey; result.selectedTaskIds = [];
          } else {
            result.tasks = [{ taskId: 'account:daily', title: 'Controlled daily task',
              titleLocales: { 'zh-CN': '受控每日任务', 'en-US': 'Controlled daily task' },
              isSelected: true, isEnabled: true, dependencies: [], retrySafety: 'unknown' }];
            result.diagnostics = [];
          }
          return result;
        };
        """;
}
