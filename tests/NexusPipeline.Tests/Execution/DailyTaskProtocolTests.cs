using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Plugins.Contracts;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public partial class DailyTaskProtocolTests
{
    [Fact]
    public async Task RecognitionSuccessPreservesUnknownFactsAndRoundTripsTheNewReport()
    {
        foreach (bool known in new[] { false, true })
        {
            var task = Task("daily") with { Recognition = "known", LifecycleRole = "business", SelectionPolicy = "managed" };
            var opaque = task with { Id = "opaque", SourceKey = "opaque", RetryUnitId = "opaque", Recognition = "opaque",
                SelectionPolicy = "preserve", CountsAsUnit = false, RequiredForParent = false, Detection = "unsupported",
                CompletionPolicy = null, WorkflowRole = null, ObservationContract = null, RetryPolicy = null };
            var discovery = new TaskDiscovery { ProtocolVersion = "0.3.0", Type = "discovery", Coverage = known ? "partial" : "opaque",
                Tasks = known ? [task, opaque] : [opaque], Diagnostics = [], Verification = new([]),
                ConfigAssessment = new("2", [new("fixture", "satisfied", "info", "none", new() { ["kind"] = "binding" }, [], [])]) };
            string observer = """
                console.log(JSON.stringify({protocolVersion:'0.3.0',type:'observation',runId:input.runId,attemptId:input.attemptId,
                  runBoundary:'open',boundaryEvidence:[],diagnostics:[],observations:input.originalPlan.tasks.some(t=>t.id==='daily')
                    ? input.logBatch.records.map(function(line,index){return {id:'o-'+line.sequence,taskId:'daily',executionOrdinal:1,
                      status:index===0?'running':'succeeded',factKind:index===0?'scope_started':'flow_ended',reasonCode:'fixture',
                      evidence:[{sourceId:line.sourceId,epoch:line.epoch,sequence:line.sequence,ruleId:index===0?'start':'end'}]};}) : []}));
                """;
            var protocol = new TaskProtocolDescriptor("0.3.0", "console.log(" + TaskProtocolJson.Write(TaskProtocolJson.Write(discovery)) + ");", observer, "", [])
                { ConfigRules = [new("fixture", true, "advisory_or_contextual") { Category = "compatibility" }] };
            string root = Path.Combine(Path.GetTempPath(), "nexus-result-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string config = Path.Combine(root, "settings.json"); File.WriteAllText(config, "{}");
                var script = new NexusPipeline.Modules.Scripts.ScriptInstance { Id = "script", RootPath = root, ConfigPath = config, PluginType = "fixture" };
                var spec = new NexusPipeline.Modules.Scripts.Contracts.ResolvedScriptSpec(script, "1", new(false, "", "", "", ""), "fixture") { TaskProtocol = protocol };
                string id = Guid.NewGuid().ToString("N");
                var run = new TaskProtocolRun(spec, id, "user", Path.Combine(root, "selection"));
                await run.BeginAsync(1, default);
                run.Append("file", "start\nend\n");
                Assert.Null((await run.ObserveAsync(true, default)).JudgeError);
                run.SetProcessCompletion(new("normal", "owned_worker_exit", 0));
                var result = run.Finish(NexusPipeline.Modules.Execution.RunAttemptResult.Partial("observed"), 1);
                Assert.Equal("success", result.Status);
                Assert.Equal(known ? "execution.success_with_partial_verification" : "execution.success_process_exit_only", result.ReasonCode);
                var report = run.Snapshot()!;
                Assert.Equal("unknown", report["finalTaskResults"]!.AsArray().Single(node => node!["taskId"]!.GetValue<string>() == "opaque")!["status"]!.GetValue<string>());
                Assert.Equal(known ? 1 : 0, report["summary"]!["counts"]!["succeeded"]!.GetValue<int>());
                var record = new NexusPipeline.Modules.History.RunRecord { Id = id, StartTime = DateTime.Now, EndTime = DateTime.Now, Status = result.Status,
                    ScriptInstanceId = "script", UserId = "user", TaskReport = report };
                string path = Path.Combine(root, "record.json");
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(record));
                var read = NexusPipeline.Modules.History.RunRecordFormat.Read(File.ReadAllText(path));
                Assert.Equal(3, read.TaskReport!["schemaVersion"]!.GetValue<int>());
                Assert.Equal(report.ToJsonString(), read.TaskReport.ToJsonString());
                string historyPath = Path.Combine(root, "history");
                var history = new NexusPipeline.Modules.History.RunHistoryService(historyPath);
                Assert.Null(history.Save(record, []).PersistenceWarning);
                Assert.Null(history.Save(new NexusPipeline.Modules.History.RunRecord
                {
                    Id = Guid.NewGuid().ToString("N"), StartTime = record.StartTime.AddSeconds(-1), EndTime = record.EndTime!.Value.AddSeconds(-1),
                    UserId = "user", ScriptInstanceId = "script", Status = "skipped",
                }, []).PersistenceWarning);
                var reopened = new NexusPipeline.Modules.History.RunHistoryService(historyPath);
                Assert.Equal(1, reopened.GetSuccessfulRunsByUser(record.StartTime.Date, "script")["user"]);
                Assert.Equal("ok", reopened.LatestTasks().Single(item => item.RecordId == record.Id).Tone);
                var malformed = report.DeepClone().AsObject();
                malformed["verification"]!["opaqueTaskCount"] = 0;
                Assert.False(NexusPipeline.Modules.History.RunRecordFormat.IsCurrentReport(malformed));
                malformed = report.DeepClone().AsObject(); malformed["verification"]!["completion"]!["exitCode"] = 9;
                Assert.False(NexusPipeline.Modules.History.RunRecordFormat.IsCurrentReport(malformed));
                var verification = TaskRunVerification.Create(TaskProtocolJson.Read<TaskPlan>(report["originalPlan"]!.ToJsonString()),
                    new("failed", "owned_process_nonzero", 7), false);
                Assert.Equal("failed", verification.Resolve(new("ok", "completed", new() { ["succeeded"] = known ? 1 : 0 }, false)).Status);
                foreach (string field in new[] { "tasks", "verification" })
                {
                    malformed = report.DeepClone().AsObject(); malformed["originalPlan"]![field] = null;
                    Assert.False(NexusPipeline.Modules.History.RunRecordFormat.IsCurrentReport(malformed));
                }
                malformed = report.DeepClone().AsObject(); malformed["finalTaskResults"]!.AsArray()[0] = null;
                Assert.False(NexusPipeline.Modules.History.RunRecordFormat.IsCurrentReport(malformed));
                malformed = report.DeepClone().AsObject();
                malformed["attemptReports"]!.AsArray()[0]!["taskResults"]!.AsArray()
                    .Single(node => node!["taskId"]!.GetValue<string>() == "opaque")!["status"] = "succeeded";
                Assert.False(NexusPipeline.Modules.History.RunRecordFormat.IsCurrentReport(malformed));
                malformed = report.DeepClone().AsObject(); malformed["finalTaskResults"]!.AsArray()[0]!["taskId"] = "unowned-task";
                Assert.False(NexusPipeline.Modules.History.RunRecordFormat.IsCurrentReport(malformed));
                foreach (string field in new[] { "selectionPolicy", "lifecycleRole", "retryUnitId" })
                {
                    malformed = report.DeepClone().AsObject();
                    malformed["originalPlan"]!["tasks"]!.AsArray().Single(node => node!["id"]!.GetValue<string>() == "opaque")![field] = "invalid";
                    Assert.False(NexusPipeline.Modules.History.RunRecordFormat.IsCurrentReport(malformed));
                }
                malformed = report.DeepClone().AsObject();
                malformed["originalPlan"]!["tasks"]!.AsArray()[0]!["dependencies"] = new System.Text.Json.Nodes.JsonArray("missing");
                Assert.False(NexusPipeline.Modules.History.RunRecordFormat.IsCurrentReport(malformed));
                malformed = report.DeepClone().AsObject();
                malformed["originalPlan"]!["tasks"]!.AsArray()[0]!["dependencies"] = new System.Text.Json.Nodes.JsonArray(
                    malformed["originalPlan"]!["tasks"]!.AsArray()[0]!["id"]!.GetValue<string>());
                Assert.False(NexusPipeline.Modules.History.RunRecordFormat.IsCurrentReport(malformed));
            }
            finally { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public async Task CompatibilityAssessmentKeepsOpaqueDiscoveryRunnableAndSafetyStillBlocks()
    {
        var check = new TaskConfigCheck("shape", "unknown", "warning", "warn",
            new() { ["kind"] = "binding" }, [], [])
            { ReasonText = new() { ["kind"] = "literal", ["value"] = "Unknown structure" } };
        var discovery = new TaskDiscovery { ProtocolVersion = "0.3.0", Type = "discovery", Coverage = "opaque",
            Tasks = [], Diagnostics = [], Verification = new([new("scope", "config:settings.json", true, "unknown_structure")]),
            ConfigAssessment = new("2", [check]) };
        var protocol = new TaskProtocolDescriptor("0.3.0", "console.log(" + TaskProtocolJson.Write(TaskProtocolJson.Write(discovery)) + ");", "", "", [])
            { ConfigRules = [new("shape", true, "advisory_or_contextual") { Category = "compatibility" }] };
        string root = Path.Combine(Path.GetTempPath(), "nexus-recognition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string config = Path.Combine(root, "settings.json"); File.WriteAllText(config, "{unknown syntax");
            var view = NexusPipeline.Modules.Configuration.Scripting.TaskConfigViewFactory.Capture(config, root, [], []);
            var plan = await TaskDiscoveryService.DiscoverAsync(protocol, view, "fixture", "1", "user", "script", "en-US", true, default);
            Assert.Equal("unknown", plan.CurrentReadiness!.State);
            Assert.Equal("daily-flow-v2", plan.SemanticsVersion);
            Assert.Equal("scope", Assert.Single(plan.Verification!.OpaqueScopes).Id);
            Assert.Equal("{unknown syntax", File.ReadAllText(config));
            var blocking = discovery with { ConfigAssessment = new("2", [check with { ExecutionEffect = "block" }]) };
            var critical = protocol with { ConfigRules = [new("shape", true, "critical_when_applicable") { Category = "compatibility" }] };
            Assert.Throws<InvalidDataException>(() => TaskProtocolValidation.ConfigAssessment(blocking, critical, new HashSet<string>(), new HashSet<string>()));
            critical = critical with { ConfigRules = [critical.ConfigRules[0] with { Category = "safety" }],
                DiscoverScript = "console.log(" + TaskProtocolJson.Write(TaskProtocolJson.Write(blocking)) + ");" };
            var blocked = await TaskDiscoveryService.DiscoverAsync(critical, view, "fixture", "1", "user", "script", "en-US", true, default);
            Assert.Equal("blocked", blocked.CurrentReadiness!.State);
            Assert.Throws<InvalidDataException>(() => TaskProtocolValidation.ConfigAssessment(discovery,
                protocol with { Version = "0.2.0" }, new HashSet<string>(), new HashSet<string>()));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ObserverFailureAfterSuccessfulEvidenceCannotBecomeSuccessfulRun()
    {
        var discovery = new TaskDiscovery { ProtocolVersion = "0.2.0", Type = "discovery", Coverage = "complete",
            Tasks = [Task("daily")], Diagnostics = [],
            ConfigAssessment = new("1", [new("fixture", "satisfied", "info", "none", new() { ["kind"] = "binding" }, [], [])]) };
        string observer = """
            if (input.isFinalCall) throw new Error('fixture observer failure');
            console.log(JSON.stringify({protocolVersion:'0.2.0',type:'observation',runId:input.runId,attemptId:input.attemptId,
              runBoundary:'open',boundaryEvidence:[],diagnostics:[],observations:input.logBatch.records.map(function(line,index){
                return {id:'observation-'+line.sequence,taskId:'daily',status:index===0?'running':'succeeded',executionOrdinal:1,
                  factKind:index===0?'scope_started':'flow_ended',reasonCode:'fixture',
                  evidence:[{sourceId:line.sourceId,epoch:line.epoch,sequence:line.sequence,ruleId:index===0?'start':'end'}]};})}));
            """;
        var protocol = new TaskProtocolDescriptor("0.2.0", "console.log(" + TaskProtocolJson.Write(TaskProtocolJson.Write(discovery)) + ");", observer, "", [])
            { ConfigRules = [new("fixture", true, "advisory_or_contextual")] };
        string root = Path.Combine(Path.GetTempPath(), "nexus-observer-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string config = Path.Combine(root, "settings.json"); File.WriteAllText(config, "{}");
            var script = new NexusPipeline.Modules.Scripts.ScriptInstance { Id = "script", RootPath = root, ConfigPath = config, PluginType = "fixture" };
            var spec = new NexusPipeline.Modules.Scripts.Contracts.ResolvedScriptSpec(script, "1", new(false, "", "", "", ""), "fixture") { TaskProtocol = protocol };
            var run = new TaskProtocolRun(spec, "run", "user", Path.Combine(root, "selection"));
            await run.BeginAsync(1, default);
            run.Append("file", "start\nend\n");
            Assert.Null((await run.ObserveAsync(false, default)).JudgeError);
            Assert.NotNull((await run.ObserveAsync(true, default)).JudgeError);
            var result = run.Finish(NexusPipeline.Modules.Execution.RunAttemptResult.Partial("observed"), 1);
            Assert.Equal("failed", result.Status);
            Assert.Equal("succeeded", run.Snapshot()!["finalTaskResults"]![0]!["status"]!.GetValue<string>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RecognitionContractKeepsOpaqueScopesOutsideObservationAndSelectionAuthority()
    {
        var known = Task("known") with { Recognition = "known", LifecycleRole = "business", SelectionPolicy = "managed" };
        var opaque = known with { Id = "opaque", SourceKey = "opaque", RetryUnitId = "opaque", Recognition = "opaque",
            SelectionPolicy = "preserve", CountsAsUnit = false, RequiredForParent = false, Detection = "unsupported",
            CompletionPolicy = null, WorkflowRole = null, ObservationContract = null, RetryPolicy = null };
        var discovery = new TaskDiscovery { ProtocolVersion = "0.3.0", Type = "discovery", Coverage = "partial",
            Tasks = [known, opaque], Diagnostics = [], Verification = new([]),
            SelectionFields = [new("config:a.json", new("enabled"), "selection") { TaskIds = ["known"] }] };
        TaskProtocolValidation.Discovery(discovery);
        TaskProtocolValidation.Discovery(discovery with { Coverage = "opaque", Tasks = [opaque], SelectionFields = [] });
        TaskProtocolValidation.Discovery(discovery with { Coverage = "opaque", Tasks = [], SelectionFields = [],
            Verification = new([new("scope", "config:a.json", true, "unknown_structure")]) });
        Assert.Throws<InvalidDataException>(() => TaskProtocolValidation.Discovery(discovery with { Coverage = "complete" }));
        Assert.Throws<InvalidDataException>(() => TaskProtocolValidation.Discovery(discovery with { Verification = null }));
        Assert.Throws<InvalidDataException>(() => TaskProtocolValidation.Discovery(discovery with { ProtocolVersion = "0.2.0" }));
        Assert.Throws<InvalidDataException>(() => TaskProtocolValidation.Discovery(discovery with
            { SelectionFields = [discovery.SelectionFields[0] with { TaskIds = ["opaque"] }] }));
        foreach (var invalid in new[] { opaque with { CountsAsUnit = true }, opaque with { SelectionPolicy = "managed" },
            opaque with { ObservationContract = known.ObservationContract }, opaque with { ConfigRef = "enabled" },
            known with { LifecycleRole = "finalize" } })
            Assert.Throws<InvalidDataException>(() => TaskProtocolValidation.Discovery(discovery with { Tasks = [invalid] }));
    }

    [Fact]
    public void SelfManagedPcLaunchBlocksHostLaunchButPreservesEmulatorAndGenericLaunch()
    {
        var script = new NexusPipeline.Modules.Scripts.ScriptInstance { LaunchGame = true, GameMode = "pc" };
        var spec = new NexusPipeline.Modules.Scripts.Contracts.ResolvedScriptSpec(script, "1", new(false, "", "", "", ""), "")
            { SelfManagedPcLaunch = true };
        Assert.False(NexusPipeline.Modules.Execution.ExecutionCoordinator.ShouldHostLaunchGame(script, spec));
        Assert.True(NexusPipeline.Modules.Execution.ExecutionCoordinator.ShouldHostLaunchGame(script, spec with { SelfManagedPcLaunch = false }));
        script.GameMode = "emulator";
        Assert.True(NexusPipeline.Modules.Execution.ExecutionCoordinator.ShouldHostLaunchGame(script, spec));
        script.LaunchGame = false;
        Assert.False(NexusPipeline.Modules.Execution.ExecutionCoordinator.ShouldHostLaunchGame(script, spec));
    }

    private static TaskDefinition Task(string id, string policy = "flow", string? parent = null) => new()
    {
        Id = id, SourceKey = id, Name = id, ParentId = parent, Role = "business", Enabled = true,
        Order = 0, CountsAsUnit = parent is null, RequiredForParent = true, RetryUnitId = parent ?? id,
        RetryRisk = "unsafe", Dependencies = [], Detection = "supported", WorkflowRole = "daily",
        CompletionPolicy = policy, RetryPolicy = new("selective_config", true, []),
        ObservationContract = new("fixture", ["file"], [new("start", "scope_started"),
            new("end", policy == "flow" ? "flow_ended" : "business_succeeded"),
            new("fail", "business_failed"), new("cancel", "upstream_cancelled"), new("skip", "normal_skip")]),
    };

    private static TaskRunReducer Reducer(params TaskDefinition[] tasks)
    {
        var reducer = TaskRunReducer.CreateDaily("run", new("0.2.0", "plan", "run", "fixture", "1", DateTimeOffset.UtcNow,
            "signature", "complete", tasks, []) { SemanticsVersion = "daily-flow-v1" });
        reducer.BeginAttempt("attempt", 1, tasks.Select(t => t.Id));
        return reducer;
    }

    private static void Observe(TaskRunReducer reducer, string id, string status, string kind, string rule, int sequence, int ordinal = 1, string attempt = "attempt")
    {
        reducer.Accept(new TaskObservationBatch
        {
            ProtocolVersion = reducer.OriginalPlan.ProtocolVersion, Type = "observation", RunId = "run", AttemptId = attempt,
            RunBoundary = "open", BoundaryEvidence = [], Diagnostics = [],
            Observations = [new() { Id = sequence.ToString(), TaskId = id, Status = status, FactKind = kind,
                ExecutionOrdinal = ordinal, ReasonCode = "fixture", SkipKind = status == "skipped" ? "satisfied" : null, Evidence = [new("file", 0, sequence, rule)] }],
        }, new([new("file", 0, sequence, "合成日志")], false));
    }

    [Fact]
    public void OpaqueExecutionNeverInventsTaskSuccessAndKnownLifecycleFailuresRemainVisible()
    {
        var business = Task("business") with { Recognition = "known", LifecycleRole = "business", SelectionPolicy = "managed" };
        var opaque = business with { Id = "opaque", SourceKey = "opaque", RetryUnitId = "opaque", Recognition = "opaque",
            SelectionPolicy = "preserve", CountsAsUnit = false, RequiredForParent = false, Detection = "unsupported",
            CompletionPolicy = null, WorkflowRole = null, ObservationContract = null, RetryPolicy = null };
        var finalize = business with { Id = "finalize", SourceKey = "finalize", RetryUnitId = "finalize",
            Role = "cleanup", WorkflowRole = "technical", LifecycleRole = "finalize", CountsAsUnit = false };
        foreach (bool failed in new[] { false, true })
        {
            var plan = new TaskPlan("0.3.0", "plan", "run", "fixture", "1", DateTimeOffset.UtcNow,
                "signature", "partial", [business, opaque, finalize], [])
                { SemanticsVersion = "daily-flow-v2", Verification = new([]) };
            var reducer = TaskRunReducer.CreateDaily("run", plan);
            reducer.BeginAttempt("attempt", 1, plan.Tasks.Select(task => task.Id));
            Assert.Throws<InvalidDataException>(() => Observe(reducer, "opaque", "succeeded", "flow_ended", "end", 1));
            Observe(reducer, "business", "running", "scope_started", "start", 2);
            Observe(reducer, "business", "succeeded", "flow_ended", "end", 3);
            Observe(reducer, "finalize", "running", "scope_started", "start", 4);
            Observe(reducer, "finalize", failed ? "failed" : "succeeded", failed ? "business_failed" : "flow_ended", failed ? "fail" : "end", 5);
            reducer.FinishAttempt("completed");
            Assert.Equal("unknown", reducer.Results.Single(result => result.TaskId == "opaque").Status);
            Assert.Empty(reducer.Results.Single(result => result.TaskId == "opaque").Evidence);
            var summary = reducer.Summarize("completed");
            Assert.Equal(1, summary.Counts["total"]);
            Assert.Equal(1, summary.Counts["succeeded"]);
            Assert.Equal(failed ? "bad" : "ok", summary.Tone);
        }
    }

    [Fact]
    public void AuthoritativeParentProtectsFailedChildrenButFlowParentFails()
    {
        foreach (string policy in new[] { "flow", "authoritative" })
        {
            var reducer = Reducer(Task("parent", policy), Task("child", parent: "parent"));
            Observe(reducer, "parent", "running", "scope_started", "start", 1);
            Observe(reducer, "child", "failed", "business_failed", "fail", 2);
            Observe(reducer, "parent", "succeeded", policy == "flow" ? "flow_ended" : "business_succeeded", "end", 3);
            reducer.FinishAttempt("completed");
            Assert.Equal(policy == "flow" ? "failed" : "partial", reducer.Results.Single(r => r.TaskId == "parent").Status);
            Assert.Equal("failed", reducer.Results.Single(r => r.TaskId == "child").Status);
            Assert.Equal(policy == "flow" ? "selective" : "stop", reducer.SelectRetry(3, false, false).Decision);
            Assert.Equal(policy == "flow" ? "failed" : "partial", reducer.Summarize("completed").Outcome);
        }
    }

    [Fact]
    public void ConsumptionFailureRetriesWithoutChangingRiskAndHonorsBudgetAndCancel()
    {
        var reducer = Reducer(Task("consume"), Task("other"));
        Observe(reducer, "consume", "failed", "business_failed", "fail", 1);
        Observe(reducer, "other", "running", "scope_started", "start", 2);
        Observe(reducer, "other", "succeeded", "flow_ended", "end", 3);
        reducer.FinishAttempt("completed");
        Assert.Equal("failed", reducer.Summarize("completed").Outcome);
        Assert.Equal(new[] { "consume" }, reducer.SelectRetry(3, false, false).IncludedTaskIds);
        Assert.Equal("unsafe", reducer.OriginalPlan.Tasks[0].RetryRisk);
        Assert.Equal("retry.cancelled", reducer.SelectRetry(3, true, false).ReasonCode);
        Assert.Equal("retry.budget_exhausted", reducer.SelectRetry(1, false, false).ReasonCode);
        reducer.BeginAttempt("resume", 2, ["consume", "other"], preserveCompleted: true);
        Observe(reducer, "consume", "skipped", "normal_skip", "skip", 1, attempt: "resume");
        Observe(reducer, "other", "skipped", "normal_skip", "skip", 2, attempt: "resume");
        Assert.Equal("failed", reducer.Results.Single(r => r.TaskId == "consume").Status);
        Assert.Equal("succeeded", reducer.Results.Single(r => r.TaskId == "other").Status);
        Observe(reducer, "consume", "running", "scope_started", "start", 3, attempt: "resume");
        Observe(reducer, "consume", "succeeded", "flow_ended", "end", 4, attempt: "resume");
        Assert.Equal("completed", reducer.Summarize("completed").Outcome);
    }

    [Fact]
    public void MissingBoundariesUseHostEvidenceAndCancelPreservesExistingFacts()
    {
        var reducer = Reducer(Task("started"), Task("missing"));
        Observe(reducer, "started", "running", "scope_started", "start", 1);
        reducer.FinishAttempt("completed");
        Assert.All(reducer.Results, r => Assert.Equal("failed", r.Status));
        Assert.Equal(new[] { "missing_terminal", "missing_logs" }, reducer.HostFailures.Select(e => e.Kind));
        Assert.All(reducer.Results, r => Assert.Single(r.HostEvidenceRefs!));
        var cancelled = Reducer(Task("failed"), Task("active"));
        Observe(cancelled, "failed", "failed", "upstream_cancelled", "cancel", 1);
        Observe(cancelled, "active", "running", "scope_started", "start", 2);
        cancelled.FinishAttempt("cancelled");
        Assert.Equal("failed", cancelled.Results.Single(r => r.TaskId == "failed").Status);
        Assert.Equal("cancelled", cancelled.Results.Single(r => r.TaskId == "active").Status);
        var interrupted = Reducer(Task("parent", "authoritative"), Task("child", parent: "parent"));
        Observe(interrupted, "parent", "succeeded", "business_succeeded", "end", 1);
        Observe(interrupted, "child", "running", "scope_started", "start", 2);
        var report = System.Text.Json.Nodes.JsonNode.Parse(TaskProtocolJson.Write(new
        {
            runId = "run", originalPlan = interrupted.OriginalPlan, finalTaskResults = interrupted.Results,
            attemptReports = new[] { new { attemptId = "attempt", lifecycleOutcome = "running" } },
        }))!.AsObject();
        TaskRunReducer.InterruptDailyReport(report);
        Assert.Equal("failed", report["summary"]!["outcome"]!.GetValue<string>());
        Assert.Equal("process_exit", report["hostEvidence"]![0]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void InternalRestartRecoversButConflictingTerminalFails()
    {
        var reducer = Reducer(Task("task"));
        Observe(reducer, "task", "failed", "business_failed", "fail", 1);
        Observe(reducer, "task", "running", "scope_started", "start", 2, 2);
        Observe(reducer, "task", "succeeded", "flow_ended", "end", 3, 2);
        Assert.Equal("succeeded", reducer.Results.Single().Status);
        Assert.False(reducer.Summarize("completed").Recovered);
        Observe(reducer, "task", "failed", "business_failed", "fail", 4, 2);
        Assert.Equal("tasks.conflicting_terminal", reducer.Results.Single().ReasonCode);
    }

    [Fact]
    public void UndeclaredAuthorityRejectsWholeBatchAndHostEvidenceCannotBeForged()
    {
        var reducer = Reducer(Task("task"));
        Assert.Throws<InvalidDataException>(() => Observe(reducer, "task", "succeeded", "business_succeeded", "end", 1));
        Assert.Equal("pending", reducer.Results.Single().Status);
        Assert.Throws<System.Text.Json.JsonException>(() => TaskProtocolJson.Read<TaskObservationBatch>("{\"hostEvidence\":[]}"));
        var unsupported = Task("task") with { Detection = "unsupported" };
        Assert.Throws<InvalidDataException>(() => Reducer(unsupported));
    }

    [Fact]
    public void ProviderFactsAndDailyWireCannotCrossReductionModes()
    {
        var task = Task("task") with { CompletionPolicy = null, WorkflowRole = null, ObservationContract = null, RetryPolicy = null };
        var plan = new TaskPlan("provider-execution-v1", "plan", "provider", "fixture", "1", DateTimeOffset.UtcNow,
            "signature", "complete", [task], []) { SemanticsVersion = "provider-execution-v1" };
        var provider = TaskRunReducer.CreateProvider("run", plan);
        provider.BeginAttempt("attempt", 1, ["task"]);
        var facts = new ProviderExecutionFacts("run", "attempt", [new()
        {
            Id = "event", TaskId = "task", ExecutionOrdinal = 1, Status = "running",
            ReasonCode = "provider.engine_running", Evidence = [], StructuredEvidenceRefs = ["authenticated"],
        }], "open", null);
        Assert.Throws<InvalidDataException>(() => provider.AcceptProviderFacts(facts, new HashSet<string>()));
        Assert.Equal("pending", provider.Results.Single().Status);
        Assert.Throws<InvalidDataException>(() => provider.AcceptProviderFacts(facts with { AttemptId = "other" }, new HashSet<string> { "authenticated" }));
        provider.AcceptProviderFacts(facts, new HashSet<string> { "authenticated" });
        Assert.Equal("running", provider.Results.Single().Status);
        Assert.Throws<InvalidDataException>(() => Observe(provider, "task", "succeeded", "flow_ended", "end", 1));
        var daily = Reducer(Task("task"));
        Assert.Throws<InvalidDataException>(() => daily.AcceptProviderFacts(facts, new HashSet<string> { "authenticated" }));
        foreach (string version in new[] { "0.1.0", "0.1.1", "provider-execution-v1" })
            Assert.Throws<InvalidDataException>(() => daily.Accept(new()
            {
                ProtocolVersion = version, Type = "observation", RunId = "run", AttemptId = "attempt",
                Observations = [], RunBoundary = "open", BoundaryEvidence = [], Diagnostics = [],
            }, new([], false)));
        Assert.Throws<InvalidDataException>(() => TaskRunReducer.CreateDaily("run", plan));
        Assert.Throws<InvalidDataException>(() => TaskRunReducer.CreateProvider("run", daily.OriginalPlan));
        Assert.Equal("pending", daily.Results.Single().Status);
    }

    [Fact]
    public void GapCannotBeOverwrittenAndOldProtocolCannotEnterDailyReducer()
    {
        var reducer = Reducer(Task("task"));
        Observe(reducer, "task", "running", "scope_started", "start", 1);
        reducer.RecordHostFailure("log_gap");
        Observe(reducer, "task", "succeeded", "flow_ended", "end", 2);
        Assert.Equal("failed", reducer.Results.Single().Status);
        var legacy = Task("task") with { CompletionPolicy = null, WorkflowRole = null, ObservationContract = null, RetryPolicy = null };
        Assert.Throws<InvalidDataException>(() => TaskRunReducer.CreateDaily("run",
            new("0.1.0", "plan", "run", "fixture", "1", DateTimeOffset.UtcNow,
                "signature", "complete", [legacy], [])));
    }
}
