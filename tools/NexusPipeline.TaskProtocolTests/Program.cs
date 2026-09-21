using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Plugins.Contracts;

// Explicit checkout input: never discovers a sibling repo or reads user configurations.
if (args.Length is not (2 or 4) || args[0] != "--plugin-root") throw new ArgumentException("Usage: --plugin-root <checkout>");
string root = Path.GetFullPath(args[1]);
if (args.Length == 4)
{
    if (args[2] != "--replay-manifest") throw new ArgumentException("Expected --replay-manifest");
    await ReplayAsync(root, Path.GetFullPath(args[3]));
    return;
}
string fixtures = Path.Combine(root, "tools", "task-protocol", "fixtures");
var files = Directory.GetFiles(fixtures, "*.json").Order(StringComparer.Ordinal).ToArray();
if (files.Length == 0) throw new InvalidDataException("Zero task protocol fixtures");
var artifacts = new HashSet<string>();
int passed = 0;
foreach (string file in files)
{
    var fixture = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
    string artifact = fixture["artifact"]!.GetValue<string>();
    if (artifact.IndexOfAny(['/', '\\', ':']) >= 0) throw new InvalidDataException("Fixture artifact path");
    bool example = artifact == "TaskProtocolExample" || fixture["example"]?.GetValue<bool>() == true;
    string plugin = example ? Path.Combine(root, "examples", artifact) : Path.Combine(root, "plugins", "specialized", artifact);
    var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(plugin, "plugin.json")))!.AsObject();
    var protocol = TaskProtocolManifest.Freeze(manifest, plugin)!;
    string temporary = Path.Combine(Path.GetTempPath(), "nxp-adapter-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporary);
    try
    {
        var view = new TaskConfigView();
        int index = 0;
        foreach (var entry in fixture["resources"]!.AsArray())
        {
            string path = Path.Combine(temporary, (++index).ToString());
            File.WriteAllText(path, entry!["text"]!.GetValue<string>());
            string id = entry["id"]!.GetValue<string>(), format = entry["format"]!.GetValue<string>();
            if (id.StartsWith("config:", StringComparison.Ordinal)) view.AddConfig(id, path, format);
            else view.AddResource(id, path, format);
        }
        var plan = await TaskDiscoveryService.DiscoverAsync(protocol, view, manifest["name"]?.GetValue<string>() ?? artifact,
            manifest["version"]!.GetValue<string>(), "fixture-user", "fixture-script", "zh-CN", true, default);
        Check(plan.Coverage == fixture["coverage"]!.GetValue<string>(), "discovery coverage");
        foreach (var item in fixture["tasks"]!.AsObject())
        {
            var task = plan.Tasks.Single(t => t.SourceKey == item.Key);
            Check(task.Enabled == item.Value!["enabled"]!.GetValue<bool>(), "selection " + item.Key);
            if (item.Value["detection"] is {} detection) Check(task.Detection == detection.GetValue<string>(), "detection " + item.Key);
        }
        Check(plan.Tasks.Length == fixture["tasks"]!.AsObject().Count, "task count");
        if (plan.Coverage != "unsupported")
        {
            var transaction = TaskSelectionTransaction.Freeze(Path.Combine(temporary, "journal"), view, plan.SelectionFields);
            var reducer = new TaskRunReducer("run", plan);
            var selected = plan.Tasks.Where(t => t.Enabled).Select(t => t.Id).ToArray();
            reducer.BeginAttempt("attempt", 1, selected);
            JsonObject? cursor = null;
            long sequence = 0;
            foreach (var batchNode in fixture["batches"]!.AsArray())
            {
                var records = batchNode!["lines"]!.AsArray().Select(line => new TaskLogRecord(
                    batchNode["source"]?.GetValue<string>() ?? "stdout", batchNode["epoch"]?.GetValue<int>() ?? 0, ++sequence, line!.GetValue<string>())).ToArray();
                var batch = new TaskLogBatch(records, batchNode["gap"]?.GetValue<bool>() ?? false);
                var observation = await TaskProtocolScriptRunner.ExecuteAsync<TaskObservationBatch>(protocol.ObserveScript,
                    new { protocolVersion = "1.0", phase = "observe", runId = "run", attemptId = "attempt", attemptNumber = 1,
                        originalPlan = plan, attemptTaskIds = selected, adapterState = cursor,
                        acceptedState = reducer.AcceptedResults.ToDictionary(r => r.TaskId, r => new { r.Status, r.ExecutionOrdinal }),
                        logBatch = batch, isFinalCall = false, terminationReason = "none" }, view.ReadConfig, view.ReadResource, false, default);
                reducer.Accept(observation, batch);
                // Replay must be idempotent, using the identical output and evidence.
                reducer.Accept(observation, batch);
                cursor = observation.CursorState;
                if (batchNode["boundary"] is {} boundary) Check(reducer.RunBoundary == boundary.GetValue<string>(), "batch boundary");
            }
            if (fixture["boundary"] is {} expectedBoundary) Check(reducer.RunBoundary == expectedBoundary.GetValue<string>(), "run boundary");
            reducer.FinishAttempt("completed");
            foreach (var expected in fixture["results"]!.AsObject())
            {
                string id = plan.Tasks.Single(t => t.SourceKey == expected.Key).Id;
                Check(reducer.Results.Single(t => t.TaskId == id).Status == expected.Value!.GetValue<string>(), "result " + expected.Key);
            }
            var retry = await TaskProtocolScriptRunner.ExecuteAsync<JsonObject>(protocol.RetryScript,
                new { protocolVersion = "1.0", phase = "retry", originalPlan = plan, attemptsUsed = 1, maxAttempts = 2,
                    taskStates = reducer.Results.ToDictionary(r => r.TaskId, r => r.Status), cancelled = false, budgetExhausted = false,
                    configResources = view.ConfigResources }, view.ReadConfig, view.ReadResource, false, default);
            var safe = reducer.SelectRetry(2, false, false);
            Check(retry["decision"]!.GetValue<string>() == safe.Decision, "retry decision");
            if (safe.Decision == "selective")
            {
                Check(retry["includedTaskIds"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(safe.IncludedTaskIds), "retry scope");
                var before = view.ConfigResources.ToDictionary(r => r.Id, r => view.Snapshot(r.Id));
                transaction.Apply(view, TaskProtocolJson.Read<TaskConfigPatch[]>(retry["filePatches"]!.ToJsonString()));
                transaction.Restore();
                foreach (var snapshot in before.Values) Check(JsonNode.DeepEquals(
                    new TaskConfigDocument(File.ReadAllBytes(snapshot.Path), snapshot.Format).Document,
                    new TaskConfigDocument(snapshot.Bytes, snapshot.Format).Document), "selection and unrelated fields restored");
            }
            else transaction.Restore();
            transaction.Complete();
        }
        if (!example) artifacts.Add(artifact);
        passed++;
        Console.WriteLine("PASS " + Path.GetFileName(file));
    }
    catch (Exception ex) { throw new InvalidDataException(Path.GetFileName(file) + ": " + ex.Message, ex); }
    finally { Directory.Delete(temporary, true); }
}
if (artifacts.Count != 6) throw new InvalidDataException("All six production adapters must execute");
Console.WriteLine($"Task protocol: {passed} passed, 0 skipped; {artifacts.Count} production adapters through Host Jint/reducer/config journal.");
static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

// Explicit, read-only replay of external script-instance exports. No history JSON is trusted as a verdict.
static async Task ReplayAsync(string root, string manifestPath)
{
    var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
    var reports = new JsonArray();
    foreach (var scenario in manifest["scenarios"]!.AsArray())
    {
        string artifact = scenario!["artifact"]!.GetValue<string>();
        if (artifact.IndexOfAny(['/', '\\', ':']) >= 0) throw new InvalidDataException("Replay artifact path");
        string plugin = Path.Combine(root, "plugins", "specialized", artifact);
        var pluginManifest = JsonNode.Parse(File.ReadAllText(Path.Combine(plugin, "plugin.json")))!.AsObject();
        var protocol = TaskProtocolManifest.Freeze(pluginManifest, plugin)!;
        var view = new TaskConfigView();
        foreach (var entry in scenario["resources"]!.AsArray())
        {
            string id = entry!["id"]!.GetValue<string>(), path = entry["path"]!.GetValue<string>(), format = entry["format"]!.GetValue<string>();
            if (id.StartsWith("config:", StringComparison.Ordinal)) view.AddConfig(id, path, format);
            else view.AddResource(id, path, format);
        }
        var plan = await TaskDiscoveryService.DiscoverAsync(protocol, view, pluginManifest["name"]!.GetValue<string>(),
            pluginManifest["version"]!.GetValue<string>(), "replay-user", "replay-script", "zh-CN", true, default);
        int runs = 0, ended = 0;
        foreach (var log in scenario["logs"]!.AsArray())
        {
            string path = log!["path"]!.GetValue<string>();
            var report = new JsonObject { ["artifact"] = artifact, ["logSha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), ["coverage"] = plan.Coverage };
            if (plan.Coverage != "unsupported")
            {
                var reducer = new TaskRunReducer("replay", plan);
                var selected = plan.Tasks.Where(t => t.Enabled).Select(t => t.Id).ToArray();
                reducer.BeginAttempt("attempt", 1, selected);
                JsonObject? cursor = null;
                long sequence = 0;
                var lines = File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l));
                foreach (var chunk in lines.Chunk(128))
                {
                    var records = chunk.Select(line => new TaskLogRecord(log["source"]!.GetValue<string>(), 0, ++sequence, line)).ToArray();
                    var batch = new TaskLogBatch(records, false);
                    var observation = await TaskProtocolScriptRunner.ExecuteAsync<TaskObservationBatch>(protocol.ObserveScript,
                        new { protocolVersion = "1.0", phase = "observe", runId = "replay", attemptId = "attempt", attemptNumber = 1,
                            originalPlan = plan, attemptTaskIds = selected, adapterState = cursor,
                            acceptedState = reducer.AcceptedResults.ToDictionary(r => r.TaskId, r => new { r.Status, r.ExecutionOrdinal }),
                            logBatch = batch, isFinalCall = false, terminationReason = "none" }, view.ReadConfig, view.ReadResource, false, default);
                    reducer.Accept(observation, batch); cursor = observation.CursorState;
                }
                report["boundary"] = reducer.RunBoundary;
                if (reducer.RunBoundary is "ended" or "aborted") ended++;
                string lifecycle = reducer.RunBoundary == "aborted" ? "failed" : reducer.RunBoundary == "ended" ? "completed" : "not_confirmed";
                reducer.FinishAttempt(lifecycle);
                report["results"] = JsonNode.Parse(TaskProtocolJson.Write(reducer.Results.Select(r => new { key = plan.Tasks.Single(t => t.Id == r.TaskId).SourceKey, r.Status })));
                report["summary"] = JsonNode.Parse(TaskProtocolJson.Write(reducer.Summarize(lifecycle)));
            }
            reports.Add(report); runs++;
            File.WriteAllText(manifest["report"]!.GetValue<string>(), reports.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine($"Replay {artifact}: coverage={plan.Coverage}, tasks={plan.Tasks.Length}, logs={runs}, boundaries={ended}");
    }
    File.WriteAllText(manifest["report"]!.GetValue<string>(), reports.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}
