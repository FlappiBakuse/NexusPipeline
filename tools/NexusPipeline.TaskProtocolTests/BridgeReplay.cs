using System.Text.Json.Nodes;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Execution.Monitoring;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Plugins.Contracts;

// Explicit isolated test input. No production install, game process or runtime registration.
internal static class BridgeReplay
{
    internal static async Task RunAsync(string pluginRoot, string manifestPath)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        string script = File.ReadAllText(Path.Combine(pluginRoot, "tools", "march7th-bridge", "observe.js"));
        string runId = manifest["runId"]!.GetValue<string>(), attemptId = manifest["attemptId"]!.GetValue<string>();
        var cases = manifest["cases"]!.AsArray();
        if (cases.Count == 0) throw new InvalidDataException("Zero bridge cases");
        int passed = 0;
        var reports = new JsonArray();
        foreach (var item in cases)
        {
            var scenario = item!.AsObject();
            string name = scenario["name"]!.GetValue<string>();
            var task = new TaskDefinition { Id = "opaque-task", SourceKey = scenario["key"]!.GetValue<string>(),
                Name = scenario["taskName"]!.GetValue<string>(), ParentId = null, Role = "business", Enabled = true,
                Order = 0, CountsAsUnit = true, RequiredForParent = false, RetryUnitId = "opaque-task",
                RetryRisk = "safe", Dependencies = [], Detection = "supported" };
            var other = task with { Id = "other-task", SourceKey = "checkin.other", Name = "Other candidate", RetryUnitId = "other-task", Order = 1 };
            var plan = new TaskPlan("1.1", "isolated-plan", "discovery", "prototype", "0.0.0", DateTimeOffset.UtcNow,
                "isolated-signature", "complete", [task, other], []);
            var reducer = new TaskRunReducer(runId, plan);
            reducer.BeginAttempt(attemptId, 1, [task.Id, other.Id]);
            var logs = new TaskLogBuffer();
            JsonObject? cursor = null;
            var session = scenario["session"]!.DeepClone().AsObject();
            if (scenario["bridgeDisabled"]?.GetValue<bool>() == true) session["enabled"] = false;
            string lifecycle = scenario["lifecycle"]?.GetValue<string>() ?? "completed";
            var diagnostics = new HashSet<string>();
            int observations = 0;
            async Task Drain(bool final)
            {
                var batch = logs.Peek();
                if (batch.Records.Length == 0 && !final) { logs.Acknowledge(batch); return; }
                var output = await TaskProtocolScriptRunner.ExecuteAsync<TaskObservationBatch>(script,
                    new { protocolVersion = "1.1", phase = "observe", runId, attemptId, originalPlan = plan,
                        attemptTaskIds = new[] { task.Id, other.Id }, bridgeSession = session, adapterState = cursor,
                        logBatch = batch, isFinalCall = final && lifecycle != "cancelled" },
                    _ => throw new InvalidDataException("Bridge read config"),
                    _ => throw new InvalidDataException("Bridge read resource"), false, default);
                reducer.Accept(output, batch);
                reducer.Accept(output, batch);
                observations += output.Observations.Length;
                foreach (var diagnostic in output.Diagnostics) diagnostics.Add(diagnostic.Code);
                cursor = output.CursorState;
                logs.Acknowledge(batch);
                if (!final && reducer.Results.Any(r => r.Status == "succeeded"))
                    throw new InvalidDataException(name+": committed before checking complete stream");
            }
            int index = 0;
            string source = scenario["wrongSource"]?.GetValue<bool>() == true ? "other-source" : "bridge";
            foreach (var chunk in scenario["chunks"]!.AsArray())
            {
                logs.Append(source, chunk!["text"]!.GetValue<string>(), newEpoch: scenario["newEpochAt"]?.GetValue<int>() == index++);
                await Drain(false);
            }
            if (scenario["native"] is {} native)
            {
                logs.Append("native", native.GetValue<string>());
                await Drain(false);
            }
            // EOF makes an unterminated fragment explicit. It must not become a valid result.
            logs.Append(source, "", final: true);
            await Drain(true);
            reducer.FinishAttempt(lifecycle);
            string actual = reducer.Results.Single(r => r.TaskId == task.Id).Status;
            if (reducer.Results.Single(r => r.TaskId == other.Id).Status != (lifecycle == "cancelled" ? "cancelled" : "unknown"))
                throw new InvalidDataException(name+": result leaked into another candidate");
            if (actual != scenario["expected"]!.GetValue<string>())
                throw new InvalidDataException(name+": expected "+scenario["expected"]+", got "+actual);
            if (observations > 1) throw new InvalidDataException(name+": duplicate business result");
            reports.Add(new JsonObject { ["name"] = name, ["basis"] = scenario["basis"]!.DeepClone(),
                ["status"] = actual, ["observations"] = observations,
                ["diagnostics"] = new JsonArray(diagnostics.Order().Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()) });
            passed++;
            Console.WriteLine("PASS bridge "+name);
        }
        string outputPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, "consumer-results.json");
        using var outputFile = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(outputFile);
        writer.Write(new JsonObject { ["passed"] = passed, ["skipped"] = 0, ["cases"] = reports }.ToJsonString());
        Console.WriteLine($"Bridge: {passed} passed, 0 skipped through Host TaskLogBuffer/Jint/reducer.");
    }
}
