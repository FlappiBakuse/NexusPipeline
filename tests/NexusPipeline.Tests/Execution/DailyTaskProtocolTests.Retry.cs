using System.Text.Json.Nodes;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Plugins.Contracts;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public partial class DailyTaskProtocolTests
{
    [Fact]
    public async Task RecognitionNativeResumePreservesOpaqueSelectionAndRequiresSameScopeRecovery()
    {
        var known = Task("daily") with { Recognition = "known", LifecycleRole = "business", SelectionPolicy = "managed",
            RetryPolicy = new("native_resume", true, []) };
        var opaque = known with { Id = "opaque", SourceKey = "opaque", RetryUnitId = "opaque", Order = 1,
            Recognition = "opaque", SelectionPolicy = "preserve", CountsAsUnit = false, RequiredForParent = false,
            Detection = "unsupported", CompletionPolicy = null, WorkflowRole = null, ObservationContract = null, RetryPolicy = null };
        var discovery = new TaskDiscovery { ProtocolVersion = "0.3.0", Type = "discovery", Coverage = "partial",
            Tasks = [known, opaque], Diagnostics = [], Verification = new([]),
            ConfigAssessment = new("2", [new("fixture", "satisfied", "info", "none", new() { ["kind"] = "binding" }, [], [])]) };
        const string observe = """
            console.log(JSON.stringify({protocolVersion:'0.3.0',type:'observation',runId:input.runId,attemptId:input.attemptId,
              runBoundary:'open',boundaryEvidence:[],diagnostics:[],observations:input.logBatch.records.map(line=>{
                const state=line.text, skip=state==='skipped';
                return {id:'o-'+line.sequence,taskId:'daily',executionOrdinal:input.attemptNumber,status:state,
                  factKind:state==='running'?'scope_started':state==='failed'?'business_failed':skip?'normal_skip':'flow_ended',
                  reasonCode:'fixture',skipKind:skip?'satisfied':null,
                  evidence:[{sourceId:line.sourceId,epoch:line.epoch,sequence:line.sequence,
                    ruleId:state==='running'?'start':state==='failed'?'fail':skip?'skip':'end'}]};})}));
            """;
        const string retry = """
            console.log(JSON.stringify({protocolVersion:'0.3.0',type:'retry',decision:'native_resume',reasonCode:'retry.unfinished',
              targetTaskIds:['daily'],launchScopeTaskIds:['daily','opaque'],includedTaskIds:['daily','opaque'],
              prerequisiteTaskIds:[],expandedUnitIds:[],preservedLifecycleTaskIds:[],preservedOpaqueTaskIds:['opaque'],
              preservedOpaqueScopeIds:[],filePatches:[]}));
            """;
        var protocol = new TaskProtocolDescriptor("0.3.0", "console.log(" + TaskProtocolJson.Write(TaskProtocolJson.Write(discovery)) + ");", observe, retry, [])
            { ConfigRules = [new("fixture", true, "advisory_or_contextual") { Category = "compatibility" }] };
        string root = Path.Combine(Path.GetTempPath(), "nexus-native-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string config = Path.Combine(root, "settings.json");
            const string original = "{\"daily\":true,\"opaque\":true,\"opaqueDisabled\":false,\"date\":\"2026-10-14\",\"budget\":7,\"progress\":2}";
            File.WriteAllText(config, original);
            var script = new NexusPipeline.Modules.Scripts.ScriptInstance { Id = "script", RootPath = root, ConfigPath = config, PluginType = "fixture" };
            var spec = new NexusPipeline.Modules.Scripts.Contracts.ResolvedScriptSpec(script, "1", new(false, "", "", "", ""), "fixture") { TaskProtocol = protocol };
            var run = new TaskProtocolRun(spec, Guid.NewGuid().ToString("N"), "user", Path.Combine(root, "selection"));
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                await run.BeginAsync(attempt, default);
                run.Append("file", attempt == 2 ? "skipped\n" : "running\n" + (attempt == 1 ? "failed\n" : "succeeded\n"));
                Assert.Null((await run.ObserveAsync(true, default)).JudgeError);
                run.SetProcessCompletion(new("normal", "owned_worker_exit", 0));
                Assert.Equal(attempt == 3 ? "success" : "failed", run.Finish(RunAttemptResult.Success("exit"), attempt).Status);
                Assert.Equal(original, File.ReadAllText(config));
                if (attempt < 3)
                {
                    Assert.True(await run.PrepareRetryAsync(3, false, false, default));
                    Assert.Equal("native_resume", run.Snapshot()!["attemptReports"]!.AsArray()[^1]!["retryDecision"]!["decision"]!.GetValue<string>());
                    Assert.Equal(original, File.ReadAllText(config));
                }
            }
            Assert.Null(run.Restore());
            Assert.Equal(original, File.ReadAllText(config));
            Assert.True(run.Snapshot()!["verification"]!["unknownRetainedOnRetry"]!.GetValue<bool>());
            Assert.True(NexusPipeline.Modules.History.RunRecordFormat.IsCurrentReport(run.Snapshot()!));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RecognitionReadFailuresPreserveResourceAndSafetyCategories()
    {
        foreach (var (error, expected) in new (Exception, string)[] {
            (new InvalidDataException("resource_limit: configuration depth"), "resource_limit"),
            (new IOException("private local path"), "safety_conflict"),
            (new InvalidDataException("config_unavailable: optional resource"), "config_unavailable") })
        {
            var result = await TaskProtocolScriptRunner.ExecuteAsync<System.Text.Json.Nodes.JsonObject>(
                "try { nexus.readConfig('config:main'); } catch(error) { console.log({code:error.message}); }",
                new { protocolVersion = "0.3.0", phase = "discover" }, _ => throw error, _ => throw error, false, default);
            Assert.Equal(expected, result["code"]!.GetValue<string>());
            Assert.DoesNotContain("private local path", result.ToJsonString());
        }
    }

    [Fact]
    public void RecognitionRetryClosesLifecycleDependenciesWithoutEnablingDisabledTasks()
    {
        foreach (string scenario in new[] { "enabled", "disabled_dependency", "disabled_prepare", "reversed_dependency", "opaque_unit" })
        {
            var tasks = new[] { "dependency", "prepare", "business", "opaque" }.Select((id, index) => Task(id) with
            { Recognition = "known", LifecycleRole = "business", SelectionPolicy = "managed", Order = index }).ToArray();
            tasks[0] = tasks[0] with { Enabled = scenario != "disabled_dependency" };
            tasks[1] = tasks[1] with { LifecycleRole = "prepare", Role = "technical", WorkflowRole = "technical",
                CountsAsUnit = false, Dependencies = ["dependency"], Enabled = scenario != "disabled_prepare" };
            tasks[3] = tasks[3] with { Recognition = "opaque", SelectionPolicy = "preserve", CountsAsUnit = false,
                RequiredForParent = false, Detection = "unsupported", CompletionPolicy = null, WorkflowRole = null,
                ObservationContract = null, RetryPolicy = null, RetryUnitId = scenario == "opaque_unit" ? "business" : "opaque" };
            if (scenario == "reversed_dependency") tasks = [tasks[1] with { Order = 0 }, tasks[0] with { Order = 1 }, tasks[2], tasks[3]];
            var plan = new TaskPlan("0.3.0", "plan", "run", "fixture", "1", DateTimeOffset.UtcNow,
                "signature", "partial", tasks, []) { SemanticsVersion = "daily-flow-v2", Verification = new([]) };
            var reducer = TaskRunReducer.CreateDaily("run", plan);
            reducer.BeginAttempt("attempt", 1, tasks.Where(task => task.Enabled).Select(task => task.Id));
            int sequence = 0;
            foreach (var task in tasks.Where(task => task.Enabled && task.Recognition == "known"))
            {
                Observe(reducer, task.Id, "running", "scope_started", "start", ++sequence);
                bool failed = task.Id == "business";
                Observe(reducer, task.Id, failed ? "failed" : "succeeded", failed ? "business_failed" : "flow_ended", failed ? "fail" : "end", ++sequence);
            }
            reducer.FinishAttempt("completed");
            var retry = reducer.SelectRetry(2, false, false);
            if (scenario == "disabled_dependency") Assert.Equal("stop", retry.Decision);
            else if (scenario == "reversed_dependency") Assert.Equal("retry.dependency_order_conflict", retry.ReasonCode);
            else
            {
                Assert.Equal("selective", retry.Decision);
                Assert.Equal(scenario == "disabled_prepare" ? new[] { "business", "opaque" }
                    : new[] { "dependency", "prepare", "business", "opaque" }, retry.IncludedTaskIds);
                Assert.Equal(scenario == "disabled_prepare" ? [] : new[] { "dependency" }, retry.PrerequisiteTaskIds);
                Assert.Equal(new[] { "opaque" }, retry.PreservedOpaqueTaskIds);
            }
        }
    }

    [Fact]
    public async Task OpaqueOriginalRerunRequiresFailedExecutionAndKeepsTheFiniteBudgetAndRawBytes()
    {
        var discovery = new TaskDiscovery
        {
            ProtocolVersion = "0.3.0", Type = "discovery", Coverage = "opaque", Tasks = [], Diagnostics = [],
            Verification = new([new("root", "config:settings.json", true, "unknown_structure")]),
            ConfigAssessment = new("2", [new("fixture", "unknown", "warning", "warn", new() { ["kind"] = "binding" }, [], [])
                { ReasonText = new() { ["kind"] = "literal", ["value"] = "The configuration structure is not recognized." } }]),
        };
        string observe = """
            console.log(JSON.stringify({protocolVersion:'0.3.0',type:'observation',runId:input.runId,attemptId:input.attemptId,
              runBoundary:'open',boundaryEvidence:[],diagnostics:[],observations:[]}));
            """;
        string retry = """
            if(input.executionCompletion.status!=='failed') throw Error('Unexpected execution state');
            console.log(JSON.stringify({protocolVersion:'0.3.0',type:'retry',decision:'original_rerun',reasonCode:'retry.original_rerun',
              includedTaskIds:[],targetTaskIds:[],launchScopeTaskIds:[],prerequisiteTaskIds:[],expandedUnitIds:[],filePatches:[],
              preservedLifecycleTaskIds:[],preservedOpaqueTaskIds:[],preservedOpaqueScopeIds:['root']}));
            """;
        var protocol = new TaskProtocolDescriptor("0.3.0", "console.log(" + TaskProtocolJson.Write(TaskProtocolJson.Write(discovery)) + ");", observe, retry, [])
        { ConfigRules = [new("fixture", true, "advisory_or_contextual") { Category = "compatibility" }] };
        string root = Path.Combine(Path.GetTempPath(), "nexus-opaque-rerun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string config = Path.Combine(root, "settings.json");
            const string raw = "{future format: [unknown data";
            File.WriteAllText(config, raw);
            foreach (var scenario in new (bool failed, int maximum, bool cancelled)[] { (false, 2, false), (true, 1, false), (true, 2, true), (true, 2, false) })
            {
                var script = new NexusPipeline.Modules.Scripts.ScriptInstance { Id = "script", RootPath = root, ConfigPath = config, PluginType = "fixture" };
                var spec = new NexusPipeline.Modules.Scripts.Contracts.ResolvedScriptSpec(script, "1", new(false, "", "", "", ""), "fixture") { TaskProtocol = protocol };
                var run = new TaskProtocolRun(spec, Guid.NewGuid().ToString("N"), "user", Path.Combine(root, "selection"));
                await run.BeginAsync(1, default);
                Assert.Null((await run.ObserveAsync(true, default)).JudgeError);
                run.SetProcessCompletion(scenario.failed ? new("failed", "owned_process_nonzero", 7) : new("normal", "owned_worker_exit", 0));
                Assert.Equal(scenario.failed ? "failed" : "success", run.Finish(RunAttemptResult.Success("exit"), 1).Status);
                bool expected = scenario.failed && scenario.maximum > 1 && !scenario.cancelled;
                Assert.Equal(expected, await run.PrepareRetryAsync(scenario.maximum, scenario.cancelled, false, default));
                Assert.False(run.Snapshot()!["verification"]!["unknownRetainedOnRetry"]!.GetValue<bool>());
                if (expected)
                {
                    Assert.Equal("original_rerun", run.Snapshot()!["attemptReports"]![0]!["retryDecision"]!["decision"]!.GetValue<string>());
                    await run.BeginAsync(2, default);
                    Assert.Null((await run.ObserveAsync(true, default)).JudgeError);
                    run.SetProcessCompletion(new("normal", "owned_worker_exit", 0));
                    Assert.Equal("success", run.Finish(RunAttemptResult.Success("exit"), 2).Status);
                    Assert.True(run.Snapshot()!["verification"]!["unknownRetainedOnRetry"]!.GetValue<bool>());
                }
                Assert.Null(run.Restore());
                Assert.Equal(raw, File.ReadAllText(config));
                Assert.True(NexusPipeline.Modules.History.RunRecordFormat.IsCurrentReport(run.Snapshot()!));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RecognitionManifestNegotiatesVersionAndPreservesClassifiedRulesWhenFrozen()
    {
        var manifest = JsonNode.Parse("""
            {"kind":"data-specialized","minHostVersion":"0.17.4","judgeScript":"data/observe.js",
             "taskProtocol":{"version":"0.3.0","discoverScript":"data/discover.js","retryScript":"data/retry.js",
               "readResources":[],"environmentChecks":[],"repairRules":[],
               "localization":{"defaultLocale":"en-US","messages":{"en-US":"data/i18n/en-US.json"}},
               "configRules":[{"id":"fixture","required":true,"criticality":"advisory_or_contextual","category":"compatibility"}]}}
            """)!.AsObject();
        string root = Path.Combine(Path.GetTempPath(), "nexus-manifest-recognition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data", "i18n"));
        try
        {
            foreach (string script in new[] { "observe", "discover", "retry" }) File.WriteAllText(Path.Combine(root, "data", script + ".js"), "console.log('{}');");
            File.WriteAllText(Path.Combine(root, "data", "i18n", "en-US.json"), "{\"fixture\":\"Fixture\"}");
            var frozen = NexusPipeline.Modules.Plugins.TaskProtocolManifest.Freeze(manifest, root)!;
            Assert.Equal("0.3.0", frozen.Version);
            Assert.Equal("compatibility", Assert.Single(frozen.ConfigRules).Category);
            manifest["minHostVersion"] = "0.17.3";
            Assert.False(NexusPipeline.Modules.Plugins.TaskProtocolManifest.TryValidate(manifest, out _));
            manifest["minHostVersion"] = "0.17.4";
            manifest["taskProtocol"]!["configRules"]![0]!["criticality"] = "critical_when_applicable";
            Assert.False(NexusPipeline.Modules.Plugins.TaskProtocolManifest.TryValidate(manifest, out _));
            manifest["taskProtocol"]!["configRules"]![0]!["criticality"] = "advisory_or_contextual";
            manifest["taskProtocol"]!["version"] = "0.2.0";
            Assert.False(NexusPipeline.Modules.Plugins.TaskProtocolManifest.TryValidate(manifest, out _));
            manifest["taskProtocol"]!["configRules"]![0]!.AsObject().Remove("category");
            Assert.Equal("0.2.0", NexusPipeline.Modules.Plugins.TaskProtocolManifest.Freeze(manifest, root)!.Version);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RecognitionListRetryPreservesUnknownDuplicatesAndRejectsChangesBeforeWriting()
    {
        var tasks = new[] { "A", "B" }.Select(id => Task(id) with
            { Recognition = "known", LifecycleRole = "business", SelectionPolicy = "managed" }).ToArray();
        var fields = new[] { new TaskSelectionField("config:settings.json", new JsonArray("selected"), "selection") { TaskIds = ["A", "B"] } };
        var plan = new TaskPlan("0.3.0", "plan", "run", "fixture", "1", DateTimeOffset.UtcNow, "signature", "complete", tasks, [])
            { SelectionFields = fields, Verification = new([]), SemanticsVersion = "daily-flow-v2" };
        string root = Path.Combine(Path.GetTempPath(), "nexus-retry-list-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "settings.json");
            const string original = "{\"selected\":[\"A\",\"future\",\"B\",\"future\",\"another\"],\"budget\":7,\"accountB\":[\"A\"]}";
            File.WriteAllText(path, original);
            var view = NexusPipeline.Modules.Configuration.Scripting.TaskConfigViewFactory.Capture(path, root, [], []);
            string revision = JsonNode.Parse(view.ReadConfig("config:settings.json", "0.3.0"))!["revision"]!.GetValue<string>();
            var transaction = NexusPipeline.Modules.Configuration.Exchange.TaskSelectionTransaction.Freeze(Path.Combine(root, "selection"), view, fields);
            var before = new JsonArray("A", "future", "B", "future", "another");
            foreach (var changed in new[] { new JsonArray("B"), new JsonArray("future", "B", "another"), new JsonArray("another", "B", "future", "future") })
            {
                var bad = new NexusPipeline.Modules.Configuration.Exchange.TaskConfigPatch("config:settings.json", "json", revision,
                    [new(new JsonArray("selected"), before, changed, "selection")]);
                Assert.Throws<InvalidDataException>(() => TaskRetryPatchValidation.Validate(plan, [bad]));
                Assert.Equal(original, File.ReadAllText(path));
            }
            var patch = new NexusPipeline.Modules.Configuration.Exchange.TaskConfigPatch("config:settings.json", "json", revision,
                [new(new JsonArray("selected"), before, new JsonArray("future", "B", "future", "another"), "selection")]);
            TaskRetryPatchValidation.Validate(plan, [patch]);
            transaction.Apply(view, [patch]);
            Assert.Equal(new[] { "future", "B", "future", "another" }, JsonNode.Parse(File.ReadAllText(path))!["selected"]!.AsArray().Select(n => n!.GetValue<string>()));
            transaction.Restore(); transaction.Complete();
            Assert.Equal(original, File.ReadAllText(path));
            const string booleanOriginal = "{\"selected\":[true,true,true,true],\"progress\":9}";
            File.WriteAllText(path, booleanOriginal);
            var indexedField = fields[0] with { TaskIndices = new() { ["A"] = 0, ["B"] = 2 } };
            var indexedPlan = plan with { SelectionFields = [indexedField] };
            view = NexusPipeline.Modules.Configuration.Scripting.TaskConfigViewFactory.Capture(path, root, [], []);
            revision = JsonNode.Parse(view.ReadConfig("config:settings.json", "0.3.0"))!["revision"]!.GetValue<string>();
            transaction = NexusPipeline.Modules.Configuration.Exchange.TaskSelectionTransaction.Freeze(Path.Combine(root, "selection"), view, [indexedField]);
            var valid = new NexusPipeline.Modules.Configuration.Exchange.TaskConfigPatch("config:settings.json", "json", revision,
                [new(new JsonArray("selected"), new JsonArray(true, true, true, true), new JsonArray(false, true, true, true), "selection")]);
            var invalid = valid with { Operations = [valid.Operations[0] with { Value = new JsonArray(false, false, true, true) }] };
            Assert.Throws<InvalidDataException>(() => TaskRetryPatchValidation.Validate(indexedPlan, [invalid]));
            Assert.Equal(booleanOriginal, File.ReadAllText(path));
            TaskRetryPatchValidation.Validate(indexedPlan, [valid]);
            transaction.Apply(view, [valid]);
            Assert.False(JsonNode.Parse(File.ReadAllText(path))!["selected"]![0]!.GetValue<bool>());
            Assert.True(JsonNode.Parse(File.ReadAllText(path))!["selected"]![1]!.GetValue<bool>());
            transaction.Restore(); transaction.Complete();
            Assert.Equal(booleanOriginal, File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RecognitionRetryPreservesLifecycleUnknownSelectionAndProgressAcrossRuns()
    {
        var tasks = new[] { "open", "dailyA", "dailyB", "vendorNew", "close" }.Select((id, index) => Task(id) with
        { Recognition = "known", LifecycleRole = "business", SelectionPolicy = "managed", Order = index }).ToArray();
        tasks[0] = tasks[0] with { LifecycleRole = "prepare", Role = "technical", WorkflowRole = "technical", CountsAsUnit = false };
        tasks[4] = tasks[4] with { LifecycleRole = "finalize", Role = "cleanup", WorkflowRole = "technical", CountsAsUnit = false };
        tasks[3] = tasks[3] with { Recognition = "opaque", SelectionPolicy = "preserve", CountsAsUnit = false,
            RequiredForParent = false, Detection = "unsupported", CompletionPolicy = null, WorkflowRole = null,
            ObservationContract = null, RetryPolicy = null };
        var discovery = new TaskDiscovery
        {
            ProtocolVersion = "0.3.0", Type = "discovery", Coverage = "partial", Tasks = tasks, Diagnostics = [], Verification = new([]),
            SelectionFields = tasks.Where(t => t.Recognition == "known").Select(t =>
                new TaskSelectionField("config:settings.json", new JsonArray(t.Id), "selection") { TaskIds = [t.Id] }).ToArray(),
            ConfigAssessment = new("2", [new("fixture", "satisfied", "info", "none", new() { ["kind"] = "binding" }, [], [])]),
        };
        string discover = "const plan=" + TaskProtocolJson.Write(discovery) + "; const config=nexus.readConfig('config:settings.json').document;"
            + "plan.tasks.forEach(t=>t.enabled=config[t.id]); console.log(JSON.stringify(plan));";
        string observe = """
            console.log(JSON.stringify({protocolVersion:'0.3.0',type:'observation',runId:input.runId,attemptId:input.attemptId,
              runBoundary:'open',boundaryEvidence:[],diagnostics:[],observations:input.logBatch.records.map(function(line){
                const event=JSON.parse(line.text); return {id:'event-'+line.sequence,taskId:event.id,executionOrdinal:1,
                  status:event.status,factKind:event.status==='running'?'scope_started':event.status==='failed'?'business_failed':'flow_ended',
                  reasonCode:'fixture',evidence:[{sourceId:line.sourceId,epoch:line.epoch,sequence:line.sequence,
                    ruleId:event.status==='running'?'start':event.status==='failed'?'fail':'end'}]};})}));
            """;
        string retry = """
            const resource=nexus.readConfig('config:settings.json');
            console.log(JSON.stringify({protocolVersion:'0.3.0',type:'retry',decision:'selective',reasonCode:'retry.unfinished',
              includedTaskIds:['open','dailyB','vendorNew','close'],prerequisiteTaskIds:[],expandedUnitIds:[],
              targetTaskIds:['dailyB'],launchScopeTaskIds:['open','dailyB','vendorNew','close'],
              preservedLifecycleTaskIds:['open','close'],preservedOpaqueTaskIds:['vendorNew'],preservedOpaqueScopeIds:[],
              filePatches:[{resourceId:'config:settings.json',format:'json',expectedRevision:resource.revision,
                operations:[{selector:['dailyA'],expected:true,value:false,purpose:'selection'}]}]}));
            """;
        var protocol = new TaskProtocolDescriptor("0.3.0", discover, observe, retry, [])
        { ConfigRules = [new("fixture", true, "advisory_or_contextual") { Category = "compatibility" }] };
        string root = Path.Combine(Path.GetTempPath(), "nexus-recognition-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string configPath = Path.Combine(root, "settings.json");
            const string original = "{\"open\":true,\"dailyA\":true,\"dailyB\":true,\"vendorNew\":true,\"close\":true,\"budget\":7,\"accountB\":{\"enabled\":true},\"progress\":0}";
            File.WriteAllText(configPath, original);
            for (int iteration = 0; iteration < 2; iteration++)
            {
                var script = new NexusPipeline.Modules.Scripts.ScriptInstance { Id = "script", RootPath = root, ConfigPath = configPath, PluginType = "fixture" };
                var spec = new NexusPipeline.Modules.Scripts.Contracts.ResolvedScriptSpec(script, "1", new(false, "", "", "", ""), "fixture") { TaskProtocol = protocol };
                var run = new TaskProtocolRun(spec, Guid.NewGuid().ToString("N"), "user", Path.Combine(root, "selection"));
                await run.BeginAsync(1, default);
                foreach (var task in tasks.Where(t => t.Recognition == "known"))
                    run.Append("file", TaskProtocolJson.Write(new { id = task.Id, status = "running" }) + "\n"
                        + TaskProtocolJson.Write(new { id = task.Id, status = task.Id == "dailyB" ? "failed" : "succeeded" }) + "\n");
                Assert.Null((await run.ObserveAsync(true, default)).JudgeError);
                run.SetProcessCompletion(new("normal", "owned_worker_exit", 0));
                Assert.Equal("failed", run.Finish(RunAttemptResult.Success("exit"), 1).Status);
                File.WriteAllText(configPath, File.ReadAllText(configPath).Replace("\"progress\":" + iteration, "\"progress\":" + (iteration + 1)));
                Assert.True(await run.PrepareRetryAsync(2, false, false, default));
                string selected = File.ReadAllText(configPath);
                Assert.Equal(original.Replace("\"dailyA\":true", "\"dailyA\":false").Replace("\"progress\":0", "\"progress\":" + (iteration + 1)), selected);
                await run.BeginAsync(2, default);
                foreach (string id in new[] { "open", "dailyB", "close" })
                    run.Append("file", TaskProtocolJson.Write(new { id, status = "running" }) + "\n" + TaskProtocolJson.Write(new { id, status = "succeeded" }) + "\n");
                Assert.Null((await run.ObserveAsync(true, default)).JudgeError);
                run.SetProcessCompletion(new("normal", "owned_worker_exit", 0));
                Assert.Equal("success", run.Finish(RunAttemptResult.Success("exit"), 2).Status);
                Assert.Null(run.Restore());
                Assert.Equal(original.Replace("\"progress\":0", "\"progress\":" + (iteration + 1)), File.ReadAllText(configPath));
                var report = run.Snapshot()!;
                Assert.True(report["verification"]!["unknownRetainedOnRetry"]!.GetValue<bool>());
                Assert.Equal("unknown", report["finalTaskResults"]!.AsArray().Single(t => t!["taskId"]!.GetValue<string>() == "vendorNew")!["status"]!.GetValue<string>());
                Assert.True(NexusPipeline.Modules.History.RunRecordFormat.IsCurrentReport(report));
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
