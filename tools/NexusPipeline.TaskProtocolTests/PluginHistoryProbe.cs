using System.Text.Json.Nodes;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Notifications;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Plugins.Repository;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Shared.Localization;

internal static class PluginHistoryProbe
{
    internal static async Task RunAsync(string source)
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-author-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(source, "plugin.json")))!.AsObject();
            string name = manifest["name"]!.GetValue<string>(), artifact = manifest["artifactName"]!.GetValue<string>();
            string version = manifest["version"]!.GetValue<string>();
            string plugins = Path.Combine(root, "plugins"), pending = Path.Combine(root, "pending.json"),
                ownership = Path.Combine(root, "ownership.json"), staging = Path.Combine(root, "staging"), backup = Path.Combine(root, "backup");
            string staged = Path.Combine(staging, artifact);
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(staged, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
            }
            void Apply(string action)
            {
                PluginInstallRecovery.AddPending(new PluginPendingOperation { Action = action, Name = name, ArtifactName = artifact,
                    Version = version, Kind = "data-specialized", StagedPath = staged, Phase = "pending" }, pending);
                Require(PluginInstallRecovery.ApplyPending(plugins, pending, ownership, staging, backup), action);
            }
            Apply("install");
            string installed = Path.Combine(plugins, artifact);
            var protocol = TaskProtocolManifest.Freeze(manifest, installed)!;
            string config = Path.Combine(root, "config.json");
            File.WriteAllText(config, """{"tasks":[{"id":"daily-reward","name":"每日奖励","builtin":true,"enabled":true}]}""");
            var script = new ScriptInstance { Id = "author-fixture", Name = "Author fixture", PluginType = name, ConfigPath = config, RootPath = root };
            var spec = new ResolvedScriptSpec(script, version, new(true, "javascript", "plugin-file", "", ""), "fixture") { TaskProtocol = protocol };
            var record = new RunRecord { ScriptInstanceId = script.Id, ScriptName = script.Name, UserId = "author", UserName = "Fixture account", StartTime = DateTime.Now };
            var run = new TaskProtocolRun(spec, record.Id, "author", Path.Combine(root, "journal"));
            await run.BeginAsync(1, default);
            run.Append("stdout", "TASK daily-reward FAIL\n");
            Require((await run.ObserveAsync(true, default)).JudgeError is null, "observe");
            run.Finish(RunAttemptResult.Partial("normal exit"), 1);
            Require(run.Restore() is null, "restore");
            record.TaskReport = run.Snapshot()!; record.Status = "failed"; record.EndTime = DateTime.Now;
            string history = Path.Combine(root, "history");
            RunHistoryService History() => new(history, Path.Combine(root, "output"), Path.Combine(root, "logs"));
            Require(History().Save(record, [], []).PersistenceWarning is null, "history save");
            string frozen = record.TaskReport.ToJsonString();
            // Change installed dictionaries before uninstalling through the real transaction engine.
            var dictionaries = manifest["taskProtocol"]!["localization"]!["messages"]!.AsObject();
            Require(dictionaries.Count > 0, "nonempty frozen dictionaries");
            foreach (var dictionary in dictionaries) File.WriteAllText(Path.Combine(installed, dictionary.Value!.GetValue<string>()), "{}");
            if (protocol.Version == "1.2")
                Require(record.TaskReport["originalPlan"]!["configAssessment"] is JsonObject, "version 1.2 diagnostic history");
            Apply("uninstall");
            Require(!Directory.Exists(installed) && PluginInstallRecovery.ReadOwnership(ownership).Count == 0, "uninstall ownership");
            var reloaded = History().FindById(record.Id)!;
            Require(reloaded.TaskReport!.ToJsonString() == frozen, "immutable history");
            foreach (var (locale, task, reason) in new[] { ("zh-CN", "每日奖励", "示例任务报告失败"), ("en-US", "Daily rewards", "Example task reported failure") })
            {
                using var scope = LocaleContext.Push(locale);
                string message = NotificationFormatter.Script(script, reloaded);
                Require(message.Contains(task) && message.Contains(reason) && message.Contains(record.Id), "frozen notification " + locale);
            }
            Console.WriteLine("PASS author plugin install → real Jint failure → history → dictionary replacement → transactional uninstall → restarted history → bilingual notification: " + artifact);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Require(bool condition, string stage)
    { if (!condition) throw new InvalidDataException("Author history probe failed: " + stage); }
}
