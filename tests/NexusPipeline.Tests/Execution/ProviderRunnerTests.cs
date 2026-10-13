using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Users;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Plugin.Abstractions;
using NexusPipeline.Tests.Support;
using Xunit;

namespace NexusPipeline.Tests.Execution;

public sealed class ProviderRunnerTests : IClassFixture<HostTestScope>
{
    public ProviderRunnerTests(HostTestScope host) { }

    [Fact]
    public void UnconfirmedSessionArtifactsBlockMaintenanceAndPreserveEveryByte()
    {
        using var host = System.Diagnostics.Process.GetCurrentProcess();
        var identity = NexusPipeline.Platform.Processes.ProcessIdentity.Capture(host)!.Value;
        var state = new ExecutionStateStore();
        string session = Guid.NewGuid().ToString("N");
        using var artifacts = new ProviderWorkerArtifacts(session, identity);
        string receipt = Path.Combine(artifacts.DirectoryPath, ".nxp-owned.json");
        string log = Path.Combine(artifacts.DirectoryPath, "agent.log");
        try
        {
            using (var lease = state.TryAcquireMaintenanceLease(out _)) Assert.NotNull(lease);
            File.WriteAllText(log, "owned retained diagnostic");
            artifacts.Dispose();
            byte[] original = File.ReadAllBytes(receipt);
            var isolated = Assert.Single(ProviderWorkerArtifacts.SnapshotIsolation());
            Assert.Equal("provider-artifacts-writers-unconfirmed", isolated.CauseCode);
            Assert.False(isolated.MayContinueIndependent);
            Assert.Null(state.TryAcquireMaintenanceLease(out _));
            Assert.Equal(original, File.ReadAllBytes(receipt));
            Assert.Equal("owned retained diagnostic", File.ReadAllText(log));
            using var parsed = System.Text.Json.JsonDocument.Parse(original);
            Assert.Equal(identity.StartTime, parsed.RootElement.GetProperty("host").GetProperty("StartTime").GetDateTime());
            Assert.Equal(identity.Pid, parsed.RootElement.GetProperty("worker").GetProperty("Pid").GetInt32());
        }
        finally
        {
            artifacts.Dispose();
            File.Delete(log);
            File.Delete(receipt);
            Directory.Delete(artifacts.DirectoryPath);
        }
        Assert.Empty(ProviderWorkerArtifacts.SnapshotIsolation());
        using var resumed = state.TryAcquireMaintenanceLease(out _);
        Assert.NotNull(resumed);
    }

    [Fact]
    public async Task RejectedBootstrapAndStartupCallbackCannotLeakSessionArtifacts()
    {
        using var fixture = new ProviderWorkerFixture();
        string root = Path.Combine(AppContext.BaseDirectory, ".nxp", "provider-workers");
        string[] Before() => Directory.Exists(root) ? Directory.GetDirectories(root).Order().ToArray() : [];
        string[] original = Before();
        var oversized = new ProviderWorkerPort(fixture.Root, fixture.Root, "startup", "oversized", 1, null);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => oversized.RunAsync(
            new("NexusPipeline.TestProviderWorker.exe", [], fixture.Root,
                new() { ["text"] = new string('x', PluginWorkerProtocol.MaximumFrameBytes) }) { UseSessionArtifacts = true },
            _ => ValueTask.CompletedTask, default));
        Assert.Equal("provider.bootstrap_size", error.Message);
        Assert.False(oversized.WorkerStarted);
        Assert.Equal(original, Before());

        var startup = new ProviderWorkerPort(fixture.Root, fixture.Root, "startup", "callback", 1, null,
            onStarted: _ => throw new InvalidDataException("fixture.startup_callback"));
        await Assert.ThrowsAsync<InvalidDataException>(() => startup.RunAsync(
            new("NexusPipeline.TestProviderWorker.exe", [], fixture.Root, new()) { UseSessionArtifacts = true },
            _ => ValueTask.CompletedTask, default));
        Assert.True(startup.WorkerStarted);
        Assert.True(startup.CleanupConfirmed);
        Assert.Equal(original, Before());
    }

    [Fact]
    public async Task EffectiveConfigurationReplacementFeedsRetryAndRestoresWithoutTouchingProject()
    {
        using var fixture = new ProviderWorkerFixture();
        string shared = Path.Combine(fixture.Root, "shared.json");
        File.WriteAllText(shared, "shared original");
        var script = new ScriptInstance { Id = "replace-" + Guid.NewGuid().ToString("N"), Name = "provider repair", RootPath = fixture.Root,
            ExecutionProviderId = "dummy", ExecutionProviderConfigId = "p", MaxAttempts = 2, JudgeScriptEnabled = true,
            JudgeScriptLanguage = "javascript", JudgeScript = """
                var i = JSON.parse(__NEXUS_INPUT__);
                var c = JSON.parse(nexus.readFile(i.script.ConfigPath));
                if(c.value === 'before') {
                    nexus.writeFile('configuration.json', JSON.stringify({value:'repaired'}));
                    console.log(JSON.stringify({status:'failed',reason:'repair then retry',replaceConfigs:['configuration.json']}));
                } else if(c.value === 'repaired' && i.log.indexOf('repaired') >= 0) {
                    console.log(JSON.stringify({status:'success',reason:'retry consumed repair',notifyText:'repair verified'}));
                }
                """ };
        var plan = new PluginProviderPlan("plan", "revision", "authorization", [new("writable_root", fixture.Root)], [new("task", "Task", 0)], new())
            { EffectiveConfiguration = new() { ["value"] = "before" } };
        var spec = new ResolvedScriptSpec(script, "1", new(true, "javascript", "generic-file", "", "hash"), "hash") { ProviderPlan = plan };
        var user = new ResolvedScriptUser("user", "User", new() { ScriptInstanceId = script.Id, Enabled = true }, spec);
        var providers = new Providers(fixture.Root, true, input: new() { ["fromEffectiveConfiguration"] = true });
        var coordinator = new ExecutionCoordinator(script, "manual", "", "", "User", default, null, null, null, null,
            new PluginAvailabilityPolicyTestsFixture.EmptyUserRepository(), new PluginAvailabilityPolicyTestsFixture.EmptyEmulatorSupportProviderResolver(),
            user, spec, executionProviders: providers);
        try
        {
            var record = await coordinator.RunAsync();
            Assert.Equal("success", record.Status);
            Assert.Equal(2, record.Attempts);
            Assert.Equal("repair verified", record.CustomNotifyText);
            Assert.Equal("failed", record.AttemptDetails[0].Status);
            Assert.Equal("shared original", File.ReadAllText(shared));
            Assert.False(Directory.Exists(Path.GetDirectoryName(NexusPipeline.Modules.Configuration.Exchange.ProviderEffectiveConfiguration.PathFor(script.Id, "user"))));
            Assert.Null(ConfigSessionMark.TryRead(script.Id, "user"));
            string recovered = NexusPipeline.Modules.Configuration.Exchange.ProviderEffectiveConfiguration.PathFor(script.Id, "user");
            var mark = new ConfigSessionMark { ScriptId = script.Id, UserId = "user", SessionPhase = "provider_run",
                ConfigPath = recovered, ConfigKind = "file", WritableRoot = fixture.Root, ProviderWorkersStopped = true };
            NexusPipeline.Modules.Configuration.Exchange.ProviderEffectiveConfiguration.Prepare(mark, new() { ["value"] = "original" });
            string area = NexusPipeline.Modules.Configuration.Paths.ConfigPaths.ScriptDir(script.Id, "user");
            Directory.CreateDirectory(area);
            File.WriteAllText(Path.Combine(area, "configuration.json"), "{\"value\":\"replacement\"}");
            NexusPipeline.Modules.Configuration.Exchange.ConfigSwapSession.ApplyConfigReplacements(script.Id, "user", recovered, ["configuration.json"]);
            string metaPath = Path.Combine(NexusPipeline.Modules.Configuration.Paths.ConfigPaths.ReplaceBackupDir(script.Id, "user"), ".meta");
            string metaBytes = File.ReadAllText(metaPath);
            File.WriteAllText(metaPath, new JsonObject { ["ConfigPath"] = shared, ["NewFiles"] = new JsonArray() }.ToJsonString());
            Assert.False(NexusPipeline.Modules.Configuration.Exchange.ProviderEffectiveConfiguration.RestoreAndCleanup(mark));
            Assert.Equal("shared original", File.ReadAllText(shared));
            Assert.True(File.Exists(recovered));
            File.WriteAllText(metaPath, metaBytes);
            Assert.True(NexusPipeline.Modules.Configuration.Exchange.ProviderEffectiveConfiguration.RestoreAndCleanup(mark));
            ConfigSessionMark.Clear(script.Id, "user");
        }
        finally { coordinator.DisposeScreenshots(); }
    }

    [Fact]
    public void ProviderJudgementSurvivesStorageRoundTripAndKeepsItsSourceAsset()
    {
        using var fixture = new ProviderWorkerFixture();
        var storage = new NexusPipeline.Modules.Scripts.Persistence.ScriptStorage(fixture.Root);
        var script = new ScriptInstance { Id = "provider", ExecutionProviderId = "dummy", ExecutionProviderConfigId = "p",
            JudgeScriptEnabled = true, JudgeScriptLanguage = "javascript", JudgeScript = "console.log('owned-source');",
            SuccessKeywords = "owned-success", FailureKeywords = "owned-failure" };
        storage.SaveScripts([script]);
        var loaded = Assert.Single(storage.LoadScripts());
        Assert.Equal(script.JudgeScript, loaded.JudgeScript);
        Assert.True(loaded.JudgeScriptEnabled);
        Assert.Equal("owned-success", loaded.SuccessKeywords);
        Assert.Equal("owned-failure", loaded.FailureKeywords);
        Assert.DoesNotContain("JudgeScript\"", File.ReadAllText(Path.Combine(fixture.Root, "config", "scripts.json")));
        storage.SaveScripts([loaded]);
        Assert.Equal(script.JudgeScript, storage.JudgeScripts.Load(script.Id, "javascript"));
    }

    [Fact]
    public async Task CoordinatorPublishesBoundedIndependentSnapshotsDuringTenThousandProgressFrames()
    {
        Assert.NotNull(typeof(PluginProviderWorkerRequest).GetConstructor([typeof(string), typeof(IReadOnlyList<string>), typeof(string), typeof(JsonObject)]));
        Assert.NotNull(typeof(PluginWorkerBootstrap).GetConstructor([typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(int), typeof(string), typeof(JsonObject)]));
        var legacyBootstrap = new PluginWorkerBootstrap("events", "control", "nonce", "execution", "record", 1, "session", new());
        Assert.DoesNotContain("sessionArtifactsDirectory", System.Text.Json.JsonSerializer.Serialize(legacyBootstrap, PluginWorkerProtocol.Json));
        using var fixture = new ProviderWorkerFixture();
        var script = new ScriptInstance { Id = "progress-" + Guid.NewGuid().ToString("N"), Name = "progress pressure", RootPath = fixture.Root,
            ExecutionProviderId = "dummy", ExecutionProviderConfigId = "p", MaxAttempts = 1 };
        var plan = new PluginProviderPlan("plan", "revision", "authorization", [new("writable_root", fixture.Root)], [new("task", "Task", 0)], new());
        var spec = new ResolvedScriptSpec(script, "1", new(false, "javascript", "provider", "", ""), "hash") { ProviderPlan = plan };
        var user = new ResolvedScriptUser("user", "User", new() { ScriptInstanceId = script.Id, Enabled = true }, spec);
        var reports = new List<JsonObject>();
        var coordinator = new ExecutionCoordinator(script, "manual", "", "", "User", default, null, null, null, null,
            new PluginAvailabilityPolicyTestsFixture.EmptyUserRepository(), new PluginAvailabilityPolicyTestsFixture.EmptyEmulatorSupportProviderResolver(),
            user, spec, executionProviders: new Providers(fixture.Root, true, 10000));
        coordinator.TaskReportChanged = report => reports.Add(report);
        try
        {
            var record = await coordinator.RunAsync();
            Assert.Equal("unverified", record.Status);
            Assert.InRange(reports.Count, 4, 7);
            Assert.Equal("succeeded", reports[^1]["engineStatus"]!.GetValue<string>());
            Assert.Equal(4, reports[^1]["structuredEvidence"]!.AsArray().Count);
            Assert.Equal("succeeded", record.Outcomes!.EngineStatus);
            string artifacts = File.ReadAllText(Path.Combine(fixture.Root, "artifacts-directory.txt"));
            Assert.False(Directory.Exists(artifacts));
            reports[0]["engineStatus"] = "consumer-modification";
            Assert.Equal("succeeded", reports[^1]["engineStatus"]!.GetValue<string>());
            Assert.Equal("succeeded", record.TaskReport!["engineStatus"]!.GetValue<string>());
            Assert.Contains(reports, report => report["structuredEvidence"]!.AsArray().Any(item => item?["kind"]?.GetValue<string>() == "task_event"));
        }
        finally { coordinator.DisposeScreenshots(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingRunnerPublishesHistoryAndRejectsProviderOnlySuccess(bool useWorker)
    {
        string root = Path.Combine(Path.GetTempPath(), "nxp-provider-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var workerFixture = new ProviderWorkerFixture();
            foreach (string file in Directory.EnumerateFiles(workerFixture.Root)) File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
            var script = new ScriptInstance { Id = "dummy-" + Guid.NewGuid().ToString("N"), Name = "Dummy provider", RootPath = root,
                ExecutionProviderId = "dummy", ExecutionProviderConfigId = "p", MaxAttempts = 1 };
            var plan = new PluginProviderPlan("plan", "revision", "authorization", [new("writable_root", root)],
                [new("task", "Task", 0)], new());
            var spec = new ResolvedScriptSpec(script, "1", new(false, "javascript", "provider", "", ""), "hash") { ProviderPlan = plan };
            var availability = new Providers(root, useWorker);
            var history = new PluginAvailabilityPolicyTestsFixture.CapturingHistoryStore();
            var runner = PluginAvailabilityPolicyTestsFixture.CreateRunner(history, availability);
            var user = new ResolvedScriptUser("user", "User", new UserScriptBinding { ScriptInstanceId = script.Id, Enabled = true }, spec);
            var execution = new RunningExecution { Kind = "script", TargetId = script.Id, Mode = "manual", TotalTasks = 1 };
            await runner.RunScriptAsync(execution, new(script, ["User"], ExecutionAdmissionProfile.ForScript(script, resolvedSpec: spec), 1, [user], spec));
            var record = Assert.Single(history.Records);
            Assert.Equal(useWorker ? "unverified" : "failed", record.Status);
            Assert.Equal("unverified", record.Outcomes!.BusinessVerification);
            if (useWorker)
            {
                Assert.Equal("succeeded", record.Outcomes.EngineStatus);
                Assert.Equal(1, record.TaskReport!["structuredEvidenceVersion"]!.GetValue<int>());
                Assert.NotEmpty(record.TaskReport["structuredEvidence"]!.AsArray());
                Assert.Empty(record.TaskReport["finalTaskResults"]![0]!["evidence"]!.AsArray());
                Assert.NotEmpty(record.TaskReport["finalTaskResults"]![0]!["structuredEvidenceRefs"]!.AsArray());
            }
            Assert.Single(execution.SnapshotRecords());
            Assert.NotNull(record.TaskReport);
            Assert.Equal(1, record.TaskReport["schemaVersion"]!.GetValue<int>());
            Assert.Equal("provider-execution-v1", record.TaskReport["semanticsVersion"]!.GetValue<string>());
            Assert.Equal("provider-execution-v1", record.TaskReport["originalPlan"]!["protocolVersion"]!.GetValue<string>());
            Assert.Equal("done", execution.Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserInstanceJudgementOverridesNativeFailureWithFreshLogsAndPreservesEngineFacts(bool scriptMode)
    {
        using var fixture = new ProviderWorkerFixture();
        var script = new ScriptInstance { Id = "judge-" + Guid.NewGuid().ToString("N"), Name = "provider judgement", RootPath = fixture.Root,
            ExecutionProviderId = "dummy", ExecutionProviderConfigId = "p", MaxAttempts = 1,
            SuccessKeywords = "owned-business-complete", JudgeScriptEnabled = scriptMode, JudgeScriptLanguage = "javascript",
            JudgeScript = "var i = JSON.parse(__NEXUS_INPUT__); if(i.log.indexOf('owned-business-complete')>=0 && i.provider.engineStatus==='failed') console.log(JSON.stringify({status:'success',reason:'business verified',notifyText:'done'}));" };
        var plan = new PluginProviderPlan("plan", "revision", "authorization", [new("writable_root", fixture.Root)], [new("task", "Task", 0)], new());
        var spec = new ResolvedScriptSpec(script, "1", new(scriptMode, "javascript", "generic-file", "", "hash"), "hash") { ProviderPlan = plan };
        var user = new ResolvedScriptUser("user", "User", new() { ScriptInstanceId = script.Id, Enabled = true }, spec);
        var coordinator = new ExecutionCoordinator(script, "manual", "", "", "User", default, null, null, null, null,
            new PluginAvailabilityPolicyTestsFixture.EmptyUserRepository(), new PluginAvailabilityPolicyTestsFixture.EmptyEmulatorSupportProviderResolver(),
            user, spec, executionProviders: new Providers(fixture.Root, true, input: new() { ["log"] = "owned-business-complete", ["engineFault"] = true,
                ["noiseLogCharacters"] = 2 * 1024 * 1024 + 73 }));
        try
        {
            var record = await coordinator.RunAsync();
            Assert.Equal("success", record.Status);
            Assert.Equal("failed", record.Outcomes!.EngineStatus);
            Assert.Equal("verified_succeeded", record.Outcomes.BusinessVerification);
            Assert.Equal("instance", record.TaskReport!["instanceJudgement"]!["scope"]!.GetValue<string>());
            Assert.Equal("unknown", record.TaskReport["finalTaskResults"]![0]!["status"]!.GetValue<string>());
            Assert.DoesNotContain(record.TaskReport["structuredEvidence"]!.AsArray(), item => item?["text"] is not null);
            if (scriptMode) Assert.Equal("done", record.CustomNotifyText);
        }
        finally { coordinator.DisposeScreenshots(); }
    }

    [Fact]
    public async Task CancellationAndCleanupFailureCannotBeReplacedByUserSuccess()
    {
        using var fixture = new ProviderWorkerFixture();
        foreach (bool cancelled in new[] { false, true })
        {
            using var stop = new CancellationTokenSource();
            if (cancelled) stop.CancelAfter(TimeSpan.FromSeconds(10));
            bool authenticatedLogObserved = false;
            var script = new ScriptInstance { Id = "control-" + Guid.NewGuid().ToString("N"), Name = "control truth", RootPath = fixture.Root,
                ExecutionProviderId = "dummy", ExecutionProviderConfigId = "p", MaxAttempts = 1, SuccessKeywords = "owned-business-complete" };
            var plan = new PluginProviderPlan("plan", "revision", "authorization", [new("writable_root", fixture.Root)], [new("task", "Task", 0)], new());
            var spec = new ResolvedScriptSpec(script, "1", new(false, "javascript", "generic-file", "", ""), "hash") { ProviderPlan = plan };
            var user = new ResolvedScriptUser("user", "User", new() { ScriptInstanceId = script.Id, Enabled = true }, spec);
            var providers = new Providers(fixture.Root, true, input: new() {
                ["log"] = "owned-business-complete", ["waitAfterLog"] = cancelled }, cleanup: cancelled);
            var coordinator = new ExecutionCoordinator(script, "manual", "", "", "User", stop.Token, null, null, null, null,
                new PluginAvailabilityPolicyTestsFixture.EmptyUserRepository(), new PluginAvailabilityPolicyTestsFixture.EmptyEmulatorSupportProviderResolver(),
                user, spec, attemptLogLine: (line, _, _) => {
                    if (line == "owned-business-complete") { authenticatedLogObserved = true; if (cancelled) stop.Cancel(); }
                }, executionProviders: providers);
            try
            {
                var record = await coordinator.RunAsync();
                Assert.Equal(cancelled ? "cancelled" : "failed", record.Status);
                Assert.NotEqual("success", record.Status);
                Assert.True(authenticatedLogObserved);
                Assert.True(providers.ActualWorkerCleanup);
                if (!cancelled) Assert.Equal("process_cleanup_unconfirmed", ConfigSessionMark.TryRead(script.Id, "user")!.RecoveryIsolation!.CauseCode);
            }
            finally
            {
                coordinator.DisposeScreenshots();
                if (providers.ActualWorkerCleanup == true) ConfigSessionMark.Clear(script.Id, "user");
            }
        }
    }

    private sealed class Providers(string root, bool useWorker, int progressCount = 0, JsonObject? input = null, bool cleanup = true) : IPluginAvailability, IPluginExecutionProviderResolver
    {
        internal bool? ActualWorkerCleanup;
        public bool IsKnownPlugin(string name) => name == "dummy";
        public bool IsDataSpecializedPlugin(string name) => false;
        public bool IsEnabled(string name) => name == "dummy";
        public ExecutionProviderDescriptor? ResolveExecutionProvider(string id) => id == "dummy" ? new(id, "1", root, new Provider(root, useWorker, progressCount, input, cleanup,
            result => ActualWorkerCleanup = result.CleanupConfirmed)) : null;
    }
    private sealed class Provider(string root, bool useWorker, int progressCount, JsonObject? input, bool cleanup,
        Action<PluginProviderWorkerResult> completed) : IPluginExecutionProvider
    {
        public string Id => "dummy";
        public ValueTask<PluginProviderInspection> InspectAsync(PluginProviderInspectRequest request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<PluginProviderPlan> PrepareAsync(PluginProviderPrepareRequest request, CancellationToken token) => throw new NotSupportedException();
        public async Task<PluginProviderRunResult> RunAsync(PluginProviderRunContext context, CancellationToken token)
        {
            var workerInput = input?.DeepClone().AsObject() ?? new() { ["progressCount"] = progressCount };
            if (workerInput["fromEffectiveConfiguration"]?.GetValue<bool>() == true)
                workerInput["log"] = JsonNode.Parse(File.ReadAllText(context.EffectiveConfigurationPath))!["value"]!.DeepClone();
            if (useWorker) completed(await context.Worker.RunAsync(new("NexusPipeline.TestProviderWorker.exe", [], root,
                workerInput) { UseSessionArtifacts = true }, context.PublishEvent, token));
            else await context.PublishEvent(new("completed", 1, "task", "succeeded", new JsonObject()));
            return new("succeeded", "dummy completed", cleanup);
        }
    }
}
