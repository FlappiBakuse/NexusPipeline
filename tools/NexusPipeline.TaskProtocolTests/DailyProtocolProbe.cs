using System.Text.Json.Nodes;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Contracts;

internal static class DailyProtocolProbe
{
    internal static async Task RunAsync(string plugins, string output)
    {
        if (Directory.Exists(output)) throw new IOException("Daily probe output already exists");
        Directory.CreateDirectory(output);
        string source = Path.Combine(plugins, "examples", "task-protocol", "json-id-array");
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(source, "plugin.json")))!.AsObject();
        var protocol = TaskProtocolManifest.Freeze(manifest, source)!;
        string config = Path.Combine(output, "config.json");
        File.WriteAllText(config, """{"tasks":[{"id":"a","name":"奖励","enabled":true},{"id":"b","name":"体力","enabled":true}],"counter":7}""");
        string other = Path.Combine(output, "account-b.json");
        File.WriteAllBytes(other, [0xef, 0xbb, 0xbf, 0x7b, 0x7d]);
        byte[] originalOther = File.ReadAllBytes(other);
        var script = new ScriptInstance { Id = "daily-fixture", Name = "日常合成验证", PluginType = "task-protocol-example", ConfigPath = config, RootPath = output };
        var spec = new ResolvedScriptSpec(script, "0.1.0", new(true, "javascript", "plugin-file", "", ""), "fixture") { TaskProtocol = protocol };
        var record = new RunRecord { ScriptInstanceId = script.Id, ScriptName = script.Name, UserId = "fixture-a", UserName = "合成账号", StartTime = DateTime.Now };
        var run = new TaskProtocolRun(spec, record.Id, record.UserId, Path.Combine(output, "journal"));
        await run.BeginAsync(1, default);
        run.Append("stdout", "TASK a START\nTASK a OK\nTASK b START\nTASK b FAIL\n");
        Require((await run.ObserveAsync(true, default)).JudgeError is null, "first observe");
        Require(run.Finish(RunAttemptResult.Success("normal exit"), 1).Status == "failed", "top-level failure");
        Require(await run.PrepareRetryAsync(2, false, false, default), "daily retry");
        var selected = JsonNode.Parse(File.ReadAllText(config))!;
        Require(selected["tasks"]![0]!["enabled"]!.GetValue<bool>() == false && selected["tasks"]![1]!["enabled"]!.GetValue<bool>(), "selection patch");
        selected["counter"] = 8; File.WriteAllText(config, selected.ToJsonString());
        await run.BeginAsync(2, default);
        run.Append("stdout", "TASK b START\nTASK b OK\n");
        Require((await run.ObserveAsync(true, default)).JudgeError is null, "retry observe");
        Require(run.Finish(RunAttemptResult.Success("normal exit"), 2).Status == "success", "retry result");
        Require(run.Restore() is null, "selection recovery");
        var restored = JsonNode.Parse(File.ReadAllText(config))!;
        Require(restored["tasks"]!.AsArray().All(t => t!["enabled"]!.GetValue<bool>()) && restored["counter"]!.GetValue<int>() == 8, "progress preservation");
        Require(File.ReadAllBytes(other).SequenceEqual(originalOther), "other account bytes");
        record.TaskReport = run.Snapshot(); record.Status = "success"; record.EndTime = DateTime.Now; record.Attempts = 2;
        Require(record.TaskReport!["schemaVersion"]!.GetValue<int>() == 2, "report version");
        Require(record.TaskReport["attemptReports"]!.AsArray().Count == 2, "real attempts");
        record.Outcomes = RunOutcomeProjector.Project(record, true, false);
        var history = new RunHistoryService(Path.Combine(output, "history"));
        Require(history.Save(record, [], []).PersistenceWarning is null, "history save");
        Require(history.FindById(record.Id)!.TaskReport!.ToJsonString() == record.TaskReport.ToJsonString(), "history reload");
        File.WriteAllText(Path.Combine(output, "result.json"), new JsonObject
        { ["passed"] = true, ["attempts"] = 2, ["real"] = "Jint, TaskProtocolRun, CAS/journal, history", ["upstream"] = "NOT_RUN" }.ToJsonString());
        Console.WriteLine("PASS 日常协议：真实 Jint → 失败 → 选择重试 → 进度恢复 → 报告2历史");
    }

    private static void Require(bool condition, string stage)
    { if (!condition) throw new InvalidDataException("Daily protocol probe: " + stage); }
}
