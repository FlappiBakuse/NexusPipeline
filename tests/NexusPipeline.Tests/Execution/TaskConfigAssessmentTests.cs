using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Contracts;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class TaskConfigAssessmentTests
{
    private static JsonObject Discovery(string version = "0.1.0") => JsonNode.Parse("""
        {"protocolVersion":"0.1.0","type":"discovery","coverage":"complete","tasks":[],"diagnostics":[],
         "configAssessment":{"schemaVersion":"1","checks":[{"ruleId":"target","evaluation":"satisfied","severity":"info",
         "executionEffect":"none","scope":{"kind":"binding"},"locations":[],"actions":[]}]}}
        """)!.AsObject().AlsoVersion(version);

    private static TaskProtocolDescriptor Protocol(JsonObject result, string criticality = "critical_when_applicable") =>
        new("0.1.0", "console.log(" + result.ToJsonString() + ");", "", "", [])
        { ConfigRules = [new("target", true, criticality)] };

    private static Task<TaskPlan> Discover(JsonObject result, TaskExecutionContext? context = null,
        string criticality = "critical_when_applicable") => TaskDiscoveryService.DiscoverAsync(
            Protocol(result, criticality), new TaskConfigView(), "unknown-author", "0.1.0", "user-a", "script", "en-US", true,
            default, context);

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.1")]
    [InlineData("1.2")]
    public void UnreleasedVersionsAreRejected(string version)
    {
        var node = Discovery(version);
        TaskDiscovery output = TaskProtocolJson.Read<TaskDiscovery>(node.ToJsonString());
        Assert.Throws<InvalidDataException>(() => TaskProtocolValidation.Discovery(output));
    }

    [Fact]
    public async Task CurrentVersionRequiresConfigAssessment()
    {
        var node = Discovery(); node.Remove("configAssessment");
        await Assert.ThrowsAsync<InvalidDataException>(() => Discover(node));
    }

    [Theory]
    [InlineData("complete", "satisfied", "none", "error", "ready")]
    [InlineData("partial", "satisfied", "none", "info", "ready")]
    [InlineData("complete", "violated", "block", "info", "blocked")]
    [InlineData("complete", "unknown", "block", "warning", "blocked")]
    [InlineData("partial", "unknown", "warn", "error", "unknown")]
    [InlineData("complete", "violated", "warn", "error", "attention")]
    [InlineData("complete", "not_applicable", "none", "warning", "ready")]
    public async Task ReadinessUsesExplicitEffectIndependentlyOfCoverageAndSeverity(
        string coverage, string evaluation, string effect, string severity, string readiness)
    {
        var node = Discovery(); node["coverage"] = coverage;
        var check = node["configAssessment"]!["checks"]![0]!;
        check["evaluation"] = evaluation; check["executionEffect"] = effect; check["severity"] = severity;
        check["reasonText"] = new JsonObject { ["kind"] = "literal", ["value"] = "Read-only fixture finding" };
        var plan = await Discover(node);
        Assert.Equal(coverage, plan.Coverage); Assert.Equal(readiness, plan.CurrentReadiness!.State);
        Assert.Empty(plan.Tasks); Assert.False(plan.CurrentReadiness.Stale);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("undeclared")]
    [InlineData("null-check")]
    [InlineData("unknown-task")]
    [InlineData("other-account")]
    [InlineData("undeclared-probe")]
    [InlineData("command-action")]
    [InlineData("url-action")]
    [InlineData("prototype-selector")]
    [InlineData("too-many")]
    [InlineData("reason-required")]
    [InlineData("satisfied-block")]
    public async Task MalformedOrUnauthorizedAssessmentCannotBecomeReady(string scenario)
    {
        var node = Discovery(); var checks = node["configAssessment"]!["checks"]!.AsArray(); var check = checks[0]!;
        switch (scenario)
        {
            case "missing": node.Remove("configAssessment"); break;
            case "empty": checks.Clear(); break;
            case "duplicate": checks.Add(check.DeepClone()); break;
            case "undeclared": check["ruleId"] = "not-in-manifest"; break;
            case "null-check": checks[0] = null; break;
            case "unknown-task": check["scope"] = JsonNode.Parse("""{"kind":"task","taskId":"other"}"""); break;
            case "other-account": check["locations"] = JsonNode.Parse("""[{"source":"config","resourceId":"config:other-user.json","selector":["target"]}]"""); break;
            case "undeclared-probe": check["locations"] = JsonNode.Parse("""[{"source":"environment","inspectionId":"arbitrary-path"}]"""); break;
            case "command-action": check["actions"] = JsonNode.Parse("""[{"kind":"run","command":"fixture"}]"""); break;
            case "url-action": check["actions"] = JsonNode.Parse("""[{"kind":"refresh_plan","url":"https://example.invalid"}]"""); break;
            case "prototype-selector":
                check["locations"] = JsonNode.Parse("""[{"source":"resource","resourceId":"allowed","selector":["__proto__"]}]""");
                var protocol = Protocol(node) with { ReadResources = [new("allowed", "root", "allowed.json", "json", false)] };
                await Assert.ThrowsAsync<InvalidDataException>(() => TaskDiscoveryService.DiscoverAsync(protocol, new TaskConfigView(), "unknown-author", "0.1.0", "u", "s", "en-US", true, default));
                return;
            case "too-many": for (int i = 1; i < 129; i++) checks.Add(check.DeepClone()); break;
            case "reason-required": check["evaluation"] = "unknown"; break;
            case "satisfied-block": check["executionEffect"] = "block"; break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => Discover(node));
    }

    [Fact]
    public async Task AdvisoryRuleCannotDeclareBlockingEffect()
    {
        var node = Discovery(); var check = node["configAssessment"]!["checks"]![0]!;
        check["evaluation"] = "violated"; check["executionEffect"] = "block";
        check["reasonText"] = new JsonObject { ["kind"] = "literal", ["value"] = "Finding" };
        await Assert.ThrowsAsync<InvalidDataException>(() => Discover(node, criticality: "advisory_or_contextual"));
    }

    [Fact]
    public async Task ContextChangesInvalidateReadinessWithoutChangingBusinessSignature()
    {
        var initial = TaskExecutionContext.Unknown("user-a", "script", "preview");
        var first = await Discover(Discovery(), initial);
        foreach (var changed in new[] {
            initial with { UserId = "user-b", BindingKey = "user-b:script" },
            initial with { Mode = "emulator" },
            initial with { Queue = new("queue", "yes") },
            initial with { GameTarget = new("executable", "C:/fixture/game.exe", null) },
            initial with { Cleanup = new(true, true, "none") },
            initial with { EffectiveLaunch = new("other", false, 2, true, "profile") } })
        {
            var next = await Discover(Discovery(), changed);
            Assert.NotEqual(first.CurrentReadiness!.ContextFingerprint, next.CurrentReadiness!.ContextFingerprint);
            Assert.Equal(first.Signature, next.Signature);
            Assert.DoesNotContain("C:/fixture", TaskProtocolJson.Write(next));
        }
    }

    [Fact]
    public async Task PreflightAndPostPreparationRecheckNeverInventAttemptsForBlockedConfig()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-diagnostic-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "config.json"); File.WriteAllText(path, "{\"valid\":true}");
            var node = Discovery();
            string source = "const valid=nexus.readConfig(input.configResources[0].id).document.valid; const result=" + node.ToJsonString()
                + ";if(!valid){const c=result.configAssessment.checks[0];c.evaluation='violated';c.executionEffect='block';c.reasonText={kind:'literal',value:'Fixture target invalid'};}console.log(result);";
            var protocol = Protocol(node) with { DiscoverScript = source };
            var script = new ScriptInstance { Id = "s", PluginType = "fictional", ConfigPath = path, RootPath = root };
            var spec = new ResolvedScriptSpec(script, "0.1.0", new(true, "javascript", "plugin-file", "", ""), "fixture") { TaskProtocol = protocol };
            var run = new TaskProtocolRun(spec, "r", "u", Path.Combine(root, "journal"));
            await run.PreflightAsync(default); Assert.False(run.IsAdmissionBlocked); Assert.Null(run.Snapshot());
            File.WriteAllText(path, "{\"valid\":false}");
            await Assert.ThrowsAsync<TaskAdmissionBlockedException>(() => run.BeginAsync(1, default));
            var report = run.Snapshot()!;
            Assert.True(run.AdmissionBlockedBeforeAttempt);
            Assert.Empty(report["attemptReports"]!.AsArray()); Assert.Empty(report["finalTaskResults"]!.AsArray());
            Assert.Equal("not_started", report["lifecycleOutcome"]!.GetValue<string>());
            Assert.NotNull(report["admissionBlocked"]); Assert.False(Directory.Exists(Path.Combine(root, "journal")));
            Assert.Equal("{\"valid\":false}", File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancellationDuringScriptExecutionPropagatesAsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TaskProtocolScriptRunner.ExecuteAsync<JsonObject>(
            "nexus.readConfig('main'); for(let i=0;i<100;i++) {} console.log({});", new {},
            _ => { cancellation.Cancel(); return "{}"; }, _ => "{}", true, cancellation.Token));
    }

    [Fact]
    public async Task ExpiredPreviewBudgetIsTimeoutRatherThanInvalidConfiguration()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => TaskProtocolScriptRunner.ExecuteAsync<JsonObject>(
            "nexus.readConfig('main'); for(let i=0;i<100;i++) {} console.log({});", new {},
            _ => { Thread.Sleep(2200); return "{}"; }, _ => "{}", true, default));
    }

    [Fact]
    public void SnapshotRevisionIsStableForSameBytesButCasTokensRemainViewLocal()
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-revision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "config.json"); File.WriteAllText(path, "{\"value\":1}");
            TaskConfigView Read() { var view = new TaskConfigView(); view.AddConfig("config:main", path, "json"); return view; }
            var first = Read(); var second = Read();
            Assert.Equal(first.RevisionToken, second.RevisionToken);
            Assert.NotEqual(JsonNode.Parse(first.ReadConfig("config:main"))!["revision"]!.ToString(),
                JsonNode.Parse(second.ReadConfig("config:main"))!["revision"]!.ToString());
            File.WriteAllText(path, "{\"value\":2}"); Assert.NotEqual(first.RevisionToken, Read().RevisionToken);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void HostContextDoesNotExposeRawLaunchArguments()
    {
        var script = new ScriptInstance { Id = "s", Args = "--secret TEST_SECRET", GameExe = "C:/fixture/game.exe", GameMode = "pc", LaunchGame = true };
        var context = ExecutionCoordinator.CreateTaskExecutionContext(script, null, "u", "pre_launch", "q", "yes");
        Assert.Equal("u:s", context.BindingKey); Assert.Equal("yes", context.Queue.HasFollowingWork);
        Assert.DoesNotContain("TEST_SECRET", TaskProtocolJson.Write(context));
    }
}

internal static class AssessmentTestJson
{
    internal static JsonObject AlsoVersion(this JsonObject value, string version)
    { value["protocolVersion"] = version; return value; }
}

