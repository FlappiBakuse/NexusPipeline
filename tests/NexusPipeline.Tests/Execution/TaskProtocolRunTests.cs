using System.Text.Json.Nodes;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Contracts;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class TaskProtocolRunTests
{
    [Fact]
    public async Task EmptyObservationsDoNotPublishCopiesButFinalAndUnverifiedOutcomeRemainVisible()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-empty-tick-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string config = Path.Combine(root, "config.json");
            File.WriteAllText(config, "{\"tasks\":[{\"id\":\"a\",\"enabled\":true},{\"id\":\"b\",\"enabled\":true}]}");
            var protocol = new TaskProtocolDescriptor("0.1.0", Discover, """
              console.log({protocolVersion:'0.1.0',type:'observation',runId:input.runId,attemptId:input.attemptId,
              observations:[],runBoundary:'unknown',boundaryEvidence:[],diagnostics:[],cursorState:{}});
              """, Retry, []) { ConfigRules = [new("fixture.default", true, "critical_when_applicable")] };
            var script = new ScriptInstance { Id = "fixture", PluginType = "fictional", ConfigPath = config, RootPath = root };
            var spec = new ResolvedScriptSpec(script, "1", new(true, "javascript", "plugin-file", "", ""), "fixture") { TaskProtocol = protocol };
            var run = new TaskProtocolRun(spec, "run", "user", Path.Combine(root, "journal"));
            await run.BeginAsync(1, default);
            int publications = 0;
            run.Changed += _ => publications++;
            Assert.Null((await run.ObserveAsync(false, default)).JudgeError);
            Assert.Null((await run.ObserveAsync(false, default)).JudgeError);
            Assert.Equal(0, publications);
            Assert.Null((await run.ObserveAsync(true, default)).JudgeError);
            Assert.Equal(1, publications);
            Assert.Equal("unverified", run.Finish(RunAttemptResult.Partial("normal exit"), 1).Status);
            Assert.Equal("completed", run.Snapshot()!["lifecycleOutcome"]!.GetValue<string>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, "failed")]
    [InlineData(true, "partial")]
    public async Task RetryAdmissionKeepsCompletedAttemptFactsWithoutStartingAnotherProtocolAttempt(
        bool partial, string expectedStatus)
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-retry-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string config = Path.Combine(root, "config.json");
            File.WriteAllText(config,
                "{\"blocked\":false,\"tasks\":[{\"id\":\"a\",\"enabled\":true},{\"id\":\"b\",\"enabled\":true}]}");
            string discover = Discover.Replace("selectionFields:", """
                configAssessment:{schemaVersion:'1',checks:[{ruleId:'target',evaluation:config.blocked?'violated':'satisfied',
                  severity:'info',executionEffect:config.blocked?'block':'none',scope:{kind:'binding'},
                  locations:[],actions:[],reasonText:{kind:'literal',value:'fixture'}}]},selectionFields:
                """);
            var protocol = new TaskProtocolDescriptor("0.1.0", discover,
                Observe, Retry, [])
            { ConfigRules = [new("target", true, "critical_when_applicable")] };
            var script = new ScriptInstance { Id = "fixture", PluginType = "fictional", ConfigPath = config, RootPath = root };
            var spec = new ResolvedScriptSpec(script, "1.0.0", new(true, "javascript", "plugin-file", "", ""), "fixture")
            { TaskProtocol = protocol };
            var run = new TaskProtocolRun(spec, "run", "user", Path.Combine(root, "journal"));
            await run.BeginAsync(1, default);
            run.Append("stdout", partial ? "a succeeded\nb failed\n" : "a failed\nb succeeded\n");
            Assert.Null((await run.ObserveAsync(true, default)).JudgeError);
            RunAttemptResult first = run.Finish(partial
                ? RunAttemptResult.Partial("normal exit") : RunAttemptResult.Failed("normal exit"), 1);
            Assert.Equal(expectedStatus, first.Status);
            Assert.True(await run.PrepareRetryAsync(2, false, false, default));

            JsonNode changed = JsonNode.Parse(File.ReadAllText(config))!;
            changed["blocked"] = true;
            File.WriteAllText(config, changed.ToJsonString());
            await Assert.ThrowsAsync<TaskAdmissionBlockedException>(() => run.BeginAsync(2, default));
            RunAttemptResult final = run.Finish(new RunAttemptResult
            { Status = "blocked", Reason = "blocked before launch", ReasonCode = "tasks.admission_blocked", IsFatal = true }, 2);
            Assert.Equal(first.Status, final.Status);
            Assert.Equal(first.ReasonCode, final.ReasonCode);
            Assert.True(final.IsFatal);
            JsonObject report = run.Snapshot()!;
            Assert.Single(report["attemptReports"]!.AsArray());
            Assert.Equal("run:1", report["attemptReports"]![0]!["attemptId"]!.GetValue<string>());
            Assert.NotNull(report["admissionBlocked"]);
            Assert.Equal(partial ? "warn" : "bad", report["summary"]!["tone"]!.GetValue<string>());
            Assert.Equal("failed", report["finalTaskResults"]![partial ? 1 : 0]!["status"]!.GetValue<string>());
            Assert.Null(run.Restore());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RetryPreparationAdmissionKeepsCompletedAttemptFacts()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-retry-preparation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string config = Path.Combine(root, "config.json");
            File.WriteAllText(config,
                "{\"tasks\":[{\"id\":\"a\",\"enabled\":true},{\"id\":\"b\",\"enabled\":true}]}");
            string discover = Discover.Replace("selectionFields:", """
                configAssessment:{schemaVersion:'1',checks:[{ruleId:'selection',
                  evaluation:config.tasks.some(t=>!t.enabled)?'violated':'satisfied',
                  severity:'info',executionEffect:config.tasks.some(t=>!t.enabled)?'block':'none',
                  scope:{kind:'binding'},locations:[],actions:[],reasonText:{kind:'literal',value:'fixture'}}]},selectionFields:
                """);
            var protocol = new TaskProtocolDescriptor("0.1.0", discover,
                Observe, Retry, [])
            { ConfigRules = [new("selection", true, "critical_when_applicable")] };
            var script = new ScriptInstance { Id = "fixture", PluginType = "fictional", ConfigPath = config, RootPath = root };
            var spec = new ResolvedScriptSpec(script, "1.0.0", new(true, "javascript", "plugin-file", "", ""), "fixture")
            { TaskProtocol = protocol };
            var run = new TaskProtocolRun(spec, "run", "user", Path.Combine(root, "journal"));

            await run.BeginAsync(1, default);
            run.Append("stdout", "a succeeded\nb failed\n");
            Assert.Null((await run.ObserveAsync(true, default)).JudgeError);
            RunAttemptResult first = run.Finish(RunAttemptResult.Partial("normal exit"), 1);
            Assert.Equal("partial", first.Status);
            Assert.False(await run.PrepareRetryAsync(2, false, false, default));
            Assert.True(run.IsAdmissionBlocked);

            JsonObject report = run.Snapshot()!;
            Assert.Single(report["attemptReports"]!.AsArray());
            Assert.Equal("run:1", report["attemptReports"]![0]!["attemptId"]!.GetValue<string>());
            Assert.Equal("completed", report["lifecycleOutcome"]!.GetValue<string>());
            Assert.Equal("failed", report["finalTaskResults"]![1]!["status"]!.GetValue<string>());
            Assert.Equal("stop", report["attemptReports"]![0]!["retryDecision"]!["decision"]!.GetValue<string>());
            Assert.Equal("tasks.admission_blocked", report["attemptReports"]![0]!["retryDecision"]!["reasonCode"]!.GetValue<string>());
            Assert.NotNull(report["admissionBlocked"]);
            Assert.Null(run.Restore());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeReplacementRejectsLaterEvidenceAndRetry(bool replaceAfterObservation)
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-runtime-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string config = Path.Combine(root, "config.json"), code = Path.Combine(root, "main.py");
            File.WriteAllText(config, "{\"tasks\":[{\"id\":\"daily\",\"enabled\":true}]}");
            File.WriteAllText(code, "original");
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(code))).ToLowerInvariant();
            string discover = Discover.Replace("selectionFields:",
                "configAssessment:{schemaVersion:'1',checks:[{ruleId:'runtime',evaluation:'satisfied',severity:'info',executionEffect:'none',scope:{kind:'binding'},locations:[],actions:[]}]},selectionFields:");
            var protocol = new TaskProtocolDescriptor("0.1.0", discover, Observe, Retry,
                [new("code", "root", "main.py", "text", true, hash)])
            { ConfigRules = [new("runtime", true, "critical_when_applicable")] };
            var script = new ScriptInstance { Id = "fixture", PluginType = "fictional", ConfigPath = config, RootPath = root };
            var spec = new ResolvedScriptSpec(script, "1.0.0", new(true, "javascript", "plugin-file", "", ""), "fixture") { TaskProtocol = protocol };
            var run = new TaskProtocolRun(spec, "run", "user", Path.Combine(root, "journal"));
            await run.BeginAsync(1, default);
            run.Append("stdout", "daily succeeded\n");
            if (!replaceAfterObservation) File.WriteAllText(code, "repaired by launcher");
            var observed = await run.ObserveAsync(true, default);
            if (replaceAfterObservation) Assert.Null(observed.JudgeError);
            else Assert.Equal("runtime_identity_changed", observed.JudgeError);
            File.WriteAllText(code, "repaired by launcher");
            Assert.Equal("failed", run.Finish(RunAttemptResult.Partial("normal exit"), 1).Status);
            Assert.False(await run.PrepareRetryAsync(2, false, false, default));
            var report = run.Snapshot()!;
            Assert.Equal("bad", report["summary"]!["tone"]!.GetValue<string>());
            Assert.Equal(replaceAfterObservation ? 1 : 0, report["summary"]!["counts"]!["succeeded"]!.GetValue<int>());
            Assert.Equal(0, report["summary"]!["counts"]!["failed"]!.GetValue<int>());
            Assert.Contains("runtime_identity_changed", report["diagnostics"]!.ToJsonString());
            Assert.Null(run.Restore());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CurrentProtocolCarriesFrozenNamesAndDynamicReasonsThroughRealJint()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-task-text-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string config = Path.Combine(root, "config.json");
            File.WriteAllText(config, """{"tasks":[{"id":"third-party:a","enabled":true}]}""");
            var texts = new TaskDisplaySnapshot("", "", "en-US", "frozen-hash", new()
            {
                ["en-US"] = new() { ["task.name"] = "Frozen task", ["reason.done"] = "Frozen reason", ["unused"] = "Unused" }
            });
            var protocol = new TaskProtocolDescriptor("0.1.0",
                Discover.Replace("name:t.id", "name:t.id,nameText:{kind:'plugin',key:'task.name',args:{},fallback:'Original'}"),
                Observe.Replace("reasonCode:'synthetic.terminal'", "reasonCode:'synthetic.terminal',reasonText:{kind:'plugin',key:'reason.done',args:{},fallback:'Done'}"),
                Retry, []) { Localization = texts, ConfigRules = [new("fixture.default", true, "critical_when_applicable")] };
            var script = new ScriptInstance { Id = "fixture", PluginType = "third-party", ConfigPath = config, RootPath = root };
            var spec = new ResolvedScriptSpec(script, "1.0.0", new(true, "javascript", "plugin-file", "", ""), "fixture") { TaskProtocol = protocol };
            var run = new TaskProtocolRun(spec, "run", "user", Path.Combine(root, "journal"));
            texts.Messages["en-US"]["reason.done"] = "Updated package";
            await run.BeginAsync(1, default);
            run.Append("stdout", "third-party:a succeeded\n");
            Assert.Null((await run.ObserveAsync(true, default)).JudgeError);
            run.Finish(RunAttemptResult.Partial("normal exit"), 1);
            Assert.Null(run.Restore());
            var report = run.Snapshot()!;
            Assert.Equal("Frozen reason", report["displaySnapshot"]!["messages"]!["en-US"]!["reason.done"]!.GetValue<string>());
            Assert.Equal("third-party", report["displaySnapshot"]!["pluginId"]!.GetValue<string>());
            Assert.DoesNotContain("unused", report.ToJsonString());
            Assert.Equal("reason.done", report["finalTaskResults"]![0]!["reasonText"]!["key"]!.GetValue<string>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FinishedReportMarksReadinessAsHistoricalAndStale()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-task-readiness-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string config = Path.Combine(root, "config.json");
            File.WriteAllText(config, "{\"tasks\":[{\"id\":\"daily\",\"enabled\":true}]}");
            string discover = Discover.Replace(
                "selectionFields:",
                "configAssessment:{schemaVersion:'1',checks:[{ruleId:'fixture.assessment',evaluation:'satisfied',severity:'info',executionEffect:'none',scope:{kind:'binding'},locations:[],actions:[]}]},selectionFields:");
            var protocol = new TaskProtocolDescriptor(
                "0.1.0", discover, Observe, Retry, [])
            {
                ConfigRules = [new TaskConfigRuleDescriptor("fixture.assessment", true, "critical_when_applicable")],
            };
            var script = new ScriptInstance { Id = "fixture", PluginType = "fictional", ConfigPath = config, RootPath = root };
            var spec = new ResolvedScriptSpec(script, "1.0.0", new(true, "javascript", "plugin-file", "", ""), "fixture")
            { TaskProtocol = protocol };
            var run = new TaskProtocolRun(spec, "run", "user", Path.Combine(root, "journal"));

            await run.BeginAsync(1, default);
            run.Append("stdout", "daily succeeded\n");
            Assert.Null((await run.ObserveAsync(true, default)).JudgeError);
            run.Finish(RunAttemptResult.Success("done"), 1);

            JsonObject report = run.Snapshot()!;
            Assert.True(report["originalPlan"]!["currentReadiness"]!["stale"]!.GetValue<bool>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RestrictedOfficialRuntimeNeverUsesOldObserverOrSelectionRetry()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-restricted-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string config = Path.Combine(root, "config.json");
            File.WriteAllText(config, "{}");
            File.WriteAllText(Path.Combine(root, "advanced.py"), "new official source bytes");
            const string discover = """
                console.log({protocolVersion:'0.1.0',type:'discovery',coverage:'partial',
                  diagnostics:[{code:'okscript.runtime_restricted',message:'base only'}],selectionFields:[],
                  configAssessment:{schemaVersion:'1',checks:[{ruleId:'fixture.runtime',evaluation:'unknown',
                    severity:'warning',executionEffect:'warn',scope:{kind:'binding'},locations:[],actions:[],
                    reasonText:{kind:'literal',value:'New release base-only run'}}]},
                  tasks:[{id:'oknte:runtime_unverified',sourceKey:'runtime_unverified',name:'Unverified run',
                    parentId:null,role:'business',enabled:true,order:0,countsAsUnit:true,requiredForParent:true,
                    retryUnitId:'oknte:runtime_unverified',retryRisk:'unsafe',dependencies:[],detection:'unsupported'}]});
                """;
            var protocol = new TaskProtocolDescriptor("0.1.0", discover,
                "throw new Error('Old task interpreter must not run');", "throw new Error('Old retry must not run');",
                [new("runtime-code-0", "root", "advanced.py", "text", false, new string('0', 64))])
            { ConfigRules = [new("fixture.runtime", true, "critical_when_applicable")] };
            var script = new ScriptInstance { Id = "fixture", PluginType = "oknte", ConfigPath = config, RootPath = root };
            var spec = new ResolvedScriptSpec(script, "0.1.0", new(true, "javascript", "plugin-file", "", ""), "fixture")
            { TaskProtocol = protocol };
            var run = new TaskProtocolRun(spec, "run", "user", Path.Combine(root, "journal"));
            await run.PreflightAsync(default);
            await run.BeginAsync(1, default);
            run.Append("stdout", "old task success text\n");
            Assert.Null((await run.ObserveAsync(true, default)).JudgeError);
            Assert.Equal("unverified", run.Finish(RunAttemptResult.Partial("normal exit"), 1).Status);
            Assert.False(await run.PrepareRetryAsync(2, false, false, default));
            JsonObject report = run.Snapshot()!;
            Assert.Equal("unknown", report["finalTaskResults"]![0]!["status"]!.GetValue<string>());
            Assert.Equal("retry.version_restricted", report["attemptReports"]![0]!["retryDecision"]!["reasonCode"]!.GetValue<string>());
            Assert.Null(run.Restore());
        }
        finally { Directory.Delete(root, true); }
    }

    private const string Discover = """
        const resource = input.configResources[0].id;
        const config = nexus.readConfig(resource).document;
        console.log({protocolVersion:'0.1.0',type:'discovery',coverage:'complete',diagnostics:[],
          configAssessment:{schemaVersion:'1',checks:[{ruleId:'fixture.default',evaluation:'satisfied',severity:'info',executionEffect:'none',scope:{kind:'binding'},locations:[],actions:[]}]},
          selectionFields: config.tasks.map(t=>({resourceId:resource,selector:['tasks',{by:'id',value:t.id},'enabled'],purpose:'selection'})),
          tasks:config.tasks.map((t,i)=>({id:t.id,sourceKey:t.id,name:t.id,parentId:null,role:'business',enabled:t.enabled,
            order:i,countsAsUnit:true,requiredForParent:true,retryUnitId:t.id,retryRisk:'safe',dependencies:[],detection:'supported',configRef:resource}))});
        """;
    private const string Observe = """
        console.log({protocolVersion:'0.1.0',type:'observation',runId:input.runId,attemptId:input.attemptId,
          runBoundary:'open',boundaryEvidence:[],diagnostics:[],
          observations:input.logBatch.records.map(r=>{const p=r.text.split(' ');return {id:'event-'+r.sourceId+'-'+r.epoch+'-'+r.sequence,
            taskId:p[0],executionOrdinal:1,status:p[1],reasonCode:'synthetic.terminal',
            evidence:[{sourceId:r.sourceId,epoch:r.epoch,sequence:r.sequence,ruleId:'synthetic.terminal'}]};})});
        """;
    private const string Retry = """
        const id=input.configResources[0].id;const current=nexus.readConfig(id);
        const included=input.originalPlan.tasks.filter(t=>t.enabled&&['failed','blocked'].includes(input.taskStates[t.id])).map(t=>t.id);
        const operations=current.document.tasks.filter(t=>t.enabled!==included.includes(t.id)).map(t=>({selector:['tasks',{by:'id',value:t.id},'enabled'],
          expected:t.enabled,value:included.includes(t.id),purpose:'selection'}));
        console.log({protocolVersion:'0.1.0',type:'retry',decision:'selective',reasonCode:'retry.unfinished',
          includedTaskIds:included,prerequisiteTaskIds:[],expandedUnitIds:[],filePatches:[{resourceId:id,format:'json',expectedRevision:current.revision,operations}]});
        """;

    [Fact]
    public async Task RealJintRunSelectivelyRetriesAndRestoresWithFinalCounters()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-task-run-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string config = Path.Combine(root, "config.json");
            File.WriteAllText(config, "{\"tasks\":[{\"id\":\"a\",\"enabled\":true},{\"id\":\"b\",\"enabled\":true}],\"count\":0}");
            var script = new ScriptInstance { Id = "fixture", PluginType = "fictional", ConfigPath = config, RootPath = root };
            var spec = new ResolvedScriptSpec(script, "1.0.0", new(true, "javascript", "plugin-file", "", ""), "fixture")
            { TaskProtocol = new("0.1.0", Discover, Observe, Retry, []) { ConfigRules = [new("fixture.default", true, "critical_when_applicable")] } };
            var run = new TaskProtocolRun(spec, "run", "user", Path.Combine(root, "journal"));
            await run.BeginAsync(1, default);
            run.Append("stdout", "a succeeded\nb failed\n");
            Assert.Null((await run.ObserveAsync(true, default)).JudgeError);
            Assert.Equal("partial", run.Finish(RunAttemptResult.Partial("normal exit"), 1).Status);
            Assert.True(await run.PrepareRetryAsync(2, false, false, default));
            var selected = JsonNode.Parse(File.ReadAllText(config))!;
            Assert.False(selected["tasks"]![0]!["enabled"]!.GetValue<bool>());
            Assert.True(selected["tasks"]![1]!["enabled"]!.GetValue<bool>());
            await run.BeginAsync(2, default);
            run.Append("stdout", "b succeeded\n");
            Assert.Null((await run.ObserveAsync(true, default)).JudgeError);
            Assert.Equal("success", run.Finish(RunAttemptResult.Partial("normal exit"), 2).Status);
            selected["count"] = 42; File.WriteAllText(config, selected.ToJsonString());
            Assert.Null(run.Restore());
            var restored = JsonNode.Parse(File.ReadAllText(config))!;
            Assert.True(restored["tasks"]![0]!["enabled"]!.GetValue<bool>());
            Assert.Equal(42, restored["count"]!.GetValue<int>());
            var report = run.Snapshot()!;
            Assert.Equal("ok", report["summary"]!["tone"]!.GetValue<string>());
            Assert.True(report["summary"]!["recovered"]!.GetValue<bool>());
            Assert.Equal(2, ((JsonArray)report["attemptReports"]!).Count);
            Assert.DoesNotContain("expectedRevision", report.ToJsonString());
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task BehaviorChangesInvalidateSignatureWhileCountersAndObjectOrderDoNot()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-task-signature-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "config.json");
            var protocol = new TaskProtocolDescriptor("0.1.0", Discover.Replace("selectionFields:",
                "behaviorFields:[{resourceId:resource,selector:['budget']}],selectionFields:"), Observe, Retry, []) { ConfigRules = [new("fixture.default", true, "critical_when_applicable")] };
            async Task<TaskPlan> Read(string json)
            {
                File.WriteAllText(path, json);
                var view = new NexusPipeline.Modules.Configuration.Scripting.TaskConfigView();
                view.AddConfig("config:config.json", path, "json");
                return await TaskDiscoveryService.DiscoverAsync(protocol, view, "fixture", "0.3.0", "user", "script", "zh-CN", true, default);
            }
            var original = await Read("{\"tasks\":[],\"budget\":{\"amount\":5,\"kind\":\"energy\"},\"count\":0}");
            var counter = await Read("{\"count\":42,\"budget\":{\"kind\":\"energy\",\"amount\":5},\"tasks\":[]}");
            var changed = await Read("{\"tasks\":[],\"budget\":{\"amount\":8,\"kind\":\"energy\"},\"count\":42}");
            Assert.Equal(original.Signature, counter.Signature);
            Assert.NotEqual(original.Signature, changed.Signature);
            Assert.DoesNotContain("energy", TaskProtocolJson.Write(original));
        }
        finally { Directory.Delete(root, true); }
    }

}
