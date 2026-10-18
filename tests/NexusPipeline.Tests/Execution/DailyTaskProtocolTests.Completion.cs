using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Plugins.Contracts;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public partial class DailyTaskProtocolTests
{
    [Fact]
    public async Task VerifiedBoundaryCompletesOnlyStoppedFullyObservedTaskRuns()
    {
        foreach (string scenario in new[] { "sample-gap", "controlled-stop", "opaque", "open", "failed-worker", "cancelled", "fatal", "stall", "live", "failed-task" })
        {
            var task = Task("daily") with { Recognition = "known", LifecycleRole = "business", SelectionPolicy = "managed" };
            var discovery = new TaskDiscovery { ProtocolVersion = "0.3.0", Type = "discovery", Coverage = "complete",
                Tasks = [task], Diagnostics = [], Verification = new(scenario == "opaque" ? [new("unknown", "config:settings.json", true, "unknown_structure")] : []),
                ConfigAssessment = new("2", [new("fixture", "satisfied", "info", "none", new() { ["kind"] = "binding" }, [], [])]) };
            if (scenario == "opaque") discovery = discovery with { Coverage = "partial" };
            string observer = """
                const records=input.logBatch.records;
                const proof=line=>({sourceId:line.sourceId,epoch:line.epoch,sequence:line.sequence,ruleId:'end'});
                console.log(JSON.stringify({protocolVersion:'0.3.0',type:'observation',runId:input.runId,attemptId:input.attemptId,
                  runBoundary:'BOUNDARY',boundaryEvidence:records.length?[proof(records[records.length-1])]:[],diagnostics:[],
                  observations:records.map((line,index)=>({id:'o-'+line.sequence,taskId:'daily',executionOrdinal:1,
                    status:index===0?'running':'FINAL_STATUS',factKind:index===0?'scope_started':'FINAL_FACT',reasonCode:'fixture',
                    evidence:[{...proof(line),ruleId:index===0?'start':'FINAL_RULE'}]}))}));
                """;
            observer = observer.Replace("BOUNDARY", scenario == "open" ? "open" : "ended")
                .Replace("FINAL_STATUS", scenario == "failed-task" ? "failed" : "succeeded")
                .Replace("FINAL_FACT", scenario == "failed-task" ? "business_failed" : "flow_ended")
                .Replace("FINAL_RULE", scenario == "failed-task" ? "fail" : "end");
            var protocol = new TaskProtocolDescriptor("0.3.0", "console.log(" + TaskProtocolJson.Write(TaskProtocolJson.Write(discovery)) + ");", observer, "", [])
                { ConfigRules = [new("fixture", true, "advisory_or_contextual") { Category = "compatibility" }] };
            string root = Path.Combine(Path.GetTempPath(), "nexus-completion-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string config = Path.Combine(root, "settings.json"); File.WriteAllText(config, "{}");
                var script = new NexusPipeline.Modules.Scripts.ScriptInstance { Id = "script", RootPath = root, ConfigPath = config, PluginType = "fixture" };
                var spec = new NexusPipeline.Modules.Scripts.Contracts.ResolvedScriptSpec(script, "1", new(false, "", "", "", ""), "fixture") { TaskProtocol = protocol };
                var run = new TaskProtocolRun(spec, Guid.NewGuid().ToString("N"), "user", Path.Combine(root, "selection"));
                await run.BeginAsync(1, default);
                run.Append("file", "start\nend\n");
                Assert.Null((await run.ObserveAsync(true, default)).JudgeError);
                run.SetTerminationReason(scenario == "stall" ? "stall" : scenario == "controlled-stop" ? "marker_timeout" : "process_exited");
                run.SetProcessCompletion(scenario == "failed-worker" ? new("failed", "owned_process_nonzero", 7)
                    : TaskExecutionCompletion.Unconfirmed with { ObservedWorkersExitedNormally = scenario != "live" });
                if (scenario == "controlled-stop")
                {
                    run.ConfirmBoundaryStop(TaskExecutionCompletion.Unconfirmed);
                    run.SetProcessCompletion(new("failed", "owned_process_nonzero", -1));
                }
                var result = run.Finish(scenario == "cancelled" ? RunAttemptResult.Cancelled("cancel")
                    : scenario == "fatal" ? RunAttemptResult.Fatal("cleanup") : RunAttemptResult.Partial("observed"), 1);
                Assert.Equal(scenario is "sample-gap" or "controlled-stop" ? "success" : scenario == "cancelled" ? "cancelled" : "failed", result.Status);
                Assert.True(NexusPipeline.Modules.History.RunRecordFormat.IsCurrentReport(run.Snapshot()!));
                if (result.Status == "success")
                {
                    Assert.Equal("verified_task_boundary", run.Snapshot()!["verification"]!["completion"]!["basis"]!.GetValue<string>());
                    Assert.False(await run.PrepareRetryAsync(3, false, false, default));
                }
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
