using System.Text.Json;
using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Execution.Judgement;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.StorageTests;

internal static class MaaWorkerSource
{
    internal static void Prepare(ProjectSourceFixture source, bool blocked, string marker, bool agent = false, bool android = false, bool preparation = false)
    {
        var catalog = source.Catalog; var definitions = new AutomationDefinitionStore(catalog.Database);
        var before = definitions.Read(); var script = before.Scripts[0];
        var original = JudgementInterpreterLease.Inspect(Environment.GetEnvironmentVariable("NEXUS_PROCESS_FIXTURE_EXE")!);
        File.Copy(original.Path, Path.Combine(source.Path, "run.exe"), true);
        foreach (var item in original.RuntimeFiles)
        {
            var path = Path.Combine(source.Path, item.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(Path.Combine(Path.GetDirectoryName(original.Path)!, item.RelativePath), path);
        }
        var changed = script with { Arguments = [], Limits = new(1, agent || preparation ? 240 : 90, agent || preparation ? 240 : 90),
            Judgement = new(AutomationJudgementLanguage.Keywords, [], [], null),
            Target = new(AutomationTargetKind.Pc, !preparation, 10, true,
                new(script.Executable, ["capture-window-fixture", marker, "static-frame"], script.Executable, AutomationPcReadiness.VisibleWindow), null) };
        if (android) changed = changed with { Target = MaaAndroidSource.Prepare(source, script.RootId) };
        var changes = new AutomationChanges(before.Revision, [], [changed], [], [], [], [], []);
        definitions.Commit(AutomationDefinitionStore.Identity(Guid.NewGuid(), "maa-worker-fixture", changes), changes, DateTimeOffset.UtcNow);
        Directory.CreateDirectory(Path.Combine(source.Path, "assets/resource/pipeline"));
        File.WriteAllBytes(Path.Combine(source.Path, "assets/interface.json"), JsonSerializer.SerializeToUtf8Bytes(new {
            interface_version = 2, name = "controlled-worker", version = "1.0",
            controller = new[] { new { name = "PC", type = android ? "Adb" : "Win32", win32 = android ? null : new { screencap = "PrintWindow", mouse = blocked ? "Interception" : "SendMessage", keyboard = "SendMessage" } } },
            resource = new[] { new { name = "Official", path = new[] { "resource" } } },
            task = new[] { new { name = "Daily", entry = "DailyEntry", default_check = true, repeatable = true } } }));
        File.WriteAllText(Path.Combine(source.Path, "assets/resource/pipeline/main.json"),
            "{\"DailyEntry\":{\"recognition\":\"DirectHit\",\"action\":\"Click\",\"target\":[123,45,1,1],\"next\":[\"Snapshot\"],\"pre_delay\":0,\"post_delay\":0}," +
            "\"Snapshot\":{\"recognition\":\"DirectHit\",\"action\":\"Screencap\",\"pre_delay\":0,\"post_delay\":0}}");
        if (android)
        {
            var path = Path.Combine(source.Path, "assets/resource/pipeline/main.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("[123,45,1,1]", "[1,1,1,1]", StringComparison.Ordinal));
        }
        File.WriteAllBytes(source.ConfigurationFile!, JsonSerializer.SerializeToUtf8Bytes(new { version = "1.0", instances = new[] {
            new { id = "one", name = "controlled", controllerName = "PC", resourceName = "Official", tasks = new[] {
                new { id = "first", taskName = "Daily", enabled = true, enabledByController = new { PC = true }, optionValues = new { } },
                new { id = "second", taskName = "Daily", enabled = true, enabledByController = new { PC = true }, optionValues = new { } } } } } }));
        var tree = new FileTreeArchive(new(SnapshotBudget.Default), new(catalog.Instance, new())).Capture(source.ConfigurationFile!, default).Snapshot;
        source.Save(new(source.Address, 1, 1, AdapterConfigurationPolicy.Contract(changed, changed.AdapterResources[0]), tree, ConfigurationUpdateKind.Manual));
    }

    internal const string Adapter = """
        globalThis.nexusTaskAdapter = input => {
          const rows = JSON.parse(nexusReadCompiled('execution', '/Tasks'));
          const result = { format: input.format, version: input.version, direction: 'response', phase: input.phase,
            extensionId: input.extensionId, operationId: input.operationId };
          if (input.phase === 'observe' || input.phase === 'retry') {
            result.runId = input.runId; result.attemptId = input.attemptId; result.planDigest = input.planDigest;
          }
          if (input.phase === 'observe') result.observations = input.tasks.map(task => ({ taskId: task.taskId,
            state: 'unknown', reasonCode: 'controlled_consumer_has_no_business_claim' }));
          else if (input.phase === 'retry') { result.idempotencyKey = input.idempotencyKey; result.selectedTaskIds = []; }
          else { result.tasks = rows.filter(row => row.Role === 'business').map(row => ({ taskId: 'consumer:' + row.Id, executionTaskId: row.Id, title: row.Name,
            isSelected: row.Enabled, isEnabled: row.Enabled, dependencies: [], retrySafety: 'unknown' })); result.diagnostics = []; }
          return result;
        };
        """;
}
