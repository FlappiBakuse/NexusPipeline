using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Plugins.Contracts;

// Explicit checkout input: never discovers a sibling repo or reads user configurations.
bool selectedMode = args.Contains("--plugin", StringComparer.Ordinal);
if ((!selectedMode && args.Length is not (2 or 4)) || args.Length < 2 || args[0] != "--plugin-root")
    throw new ArgumentException("Usage: --plugin-root <checkout> [--plugin <artifact> --report <file> [--scenario <fixture>]]");
string root = Path.GetFullPath(args[1]);
string[] supportedArtifacts = ["BetterGI", "March7thAssistant", "BAAH", "ZenlessZoneZeroOneDragon", "MaaEnd", "MaaStellaSora", "OkNTE", "OkWutheringWaves"];
string? selectedArtifact = null, selectedScenario = null, reportPath = null;
int observeCount = 0;
if (selectedMode)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    if (args.Length % 2 != 0) throw new ArgumentException("Missing option value");
    for (int i = 2; i < args.Length; i += 2)
    {
        if (args[i] is not ("--plugin" or "--report" or "--scenario" or "--observe-count") || string.IsNullOrWhiteSpace(args[i + 1])
            || !options.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Unknown or duplicate option");
    }
    if (!options.TryGetValue("--plugin", out selectedArtifact) || !supportedArtifacts.Contains(selectedArtifact, StringComparer.Ordinal)
        || !options.TryGetValue("--report", out reportPath)) throw new ArgumentException("Known plugin and report path are required");
    options.TryGetValue("--scenario", out selectedScenario);
    if (options.TryGetValue("--observe-count", out string? countText)
        && (selectedScenario is null || !int.TryParse(countText, out observeCount) || observeCount != 12))
        throw new ArgumentException("Finite observation requires one scenario and exactly 12 inputs");
    reportPath = Path.GetFullPath(reportPath);
    if (File.Exists(reportPath)) throw new IOException("Report already exists; use a new run path");
}
if (!selectedMode && args.Length == 4 && args[2] == "--report")
{
    reportPath = Path.GetFullPath(args[3]);
    if (File.Exists(reportPath)) throw new IOException("Report already exists; use a new run path");
}
else if (!selectedMode && args.Length == 4)
{
    if (args[2] == "--config-repair")
    {
        ConfigRepairProbe.Run(root, Path.GetFullPath(args[3]));
        return;
    }
    if (args[2] == "--daily-protocol")
    {
        await DailyProtocolProbe.RunAsync(root, Path.GetFullPath(args[3]));
        return;
    }
    if (args[2] == "--runtime-installations")
    {
        await RuntimeInstallationProbe.RunAsync(root, Path.GetFullPath(args[3]));
        return;
    }
    if (args[2] == "--account-isolation")
    {
        await AccountIsolationProbe.RunAsync(root, Path.GetFullPath(args[3]));
        return;
    }
    if (args[2] == "--history-plugin")
    {
        await PluginHistoryProbe.RunAsync(Path.Combine(root, "examples", args[3]));
        return;
    }
    if (args[2] != "--replay-manifest") throw new ArgumentException("Expected --replay-manifest");
    await ReplayAsync(root, Path.GetFullPath(args[3]));
    return;
}
string fixtures = Path.Combine(root, "tests", "fixtures", "task-protocol");
var files = TaskFixtures.Index(fixtures).Values.OrderBy(Path.GetFileNameWithoutExtension, StringComparer.Ordinal).ToArray();
if (selectedMode)
{
    files = files.Where(file => JsonNode.Parse(File.ReadAllText(file))?["artifact"]?.GetValue<string>() == selectedArtifact).ToArray();
    if (selectedScenario is not null)
        files = files.Where(file => Path.GetFileNameWithoutExtension(file) == selectedScenario).ToArray();
}
if (files.Length == 0) throw new InvalidDataException("Zero task protocol fixtures");
var artifacts = new HashSet<string>();
var phaseChecked = new HashSet<string>();
int passed = 0;
var failures = new List<object>();
var completedCaseIds = new List<string>();
foreach (string file in files)
{
    var fixture = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
    var fixtureResources = TaskFixtures.Read(fixture, fixtures);
    string artifact = fixture["artifact"]!.GetValue<string>();
    if (artifact.IndexOfAny(['/', '\\', ':']) >= 0) throw new InvalidDataException("Fixture artifact path");
    bool example = artifact == "TaskProtocolExample" || fixture["example"]?.GetValue<bool>() == true;
    string preset = artifact switch
    {
        "TaskProtocolExample" => "json-id-array", "TaskProtocolJsonMap" => "json-map",
        "TaskProtocolJsonParallelArray" => "json-parallel-array", "TaskProtocolYaml" => "yaml",
        "TaskProtocolMxu" => "mxu", _ when !example => "", _ => throw new InvalidDataException("Unknown current example"),
    };
    string plugin = example ? Path.Combine(root, "examples", "task-protocol", preset) : Path.Combine(root, "plugins", "specialized", artifact);
    var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(plugin, "plugin.json")))!.AsObject();
    var protocol = TaskProtocolManifest.Freeze(manifest, plugin)!;
    if (phaseChecked.Add(artifact))
    {
        foreach (var (phase, script) in new[] { ("discover", protocol.DiscoverScript), ("observe", protocol.ObserveScript), ("retry", protocol.RetryScript) })
        {
            foreach (string wrongPhase in new[] { "discover", "observe", "retry", "invalid" }.Where(p => p != phase))
            {
                bool rejected = false;
                try
                {
                    await TaskProtocolScriptRunner.ExecuteAsync<JsonObject>(script, new { phase = wrongPhase },
                        _ => throw new InvalidDataException("Unexpected config access"),
                        _ => throw new InvalidDataException("Unexpected resource access"), true, default);
                }
                catch (Exception ex) when (ex.Message.Contains("protocol_error: wrong phase", StringComparison.Ordinal)) { rejected = true; }
                Check(rejected, $"{artifact} {phase} must reject {wrongPhase} before accessing resources");
            }
            // Probe in the same Jint profile as the actual phase, without host filesystem access.
            var permissions = await TaskProtocolScriptRunner.ExecuteAsync<JsonObject>(
                "console.log({write:typeof nexus.writeFile,network:typeof nexus.httpGet,screenshot:typeof nexus.screenshot,node:typeof require,clr:typeof System});",
                new { phase }, _ => throw new InvalidDataException(), _ => throw new InvalidDataException(), true, default);
            Check(permissions.All(p => p.Value!.GetValue<string>() == "undefined"), artifact + " phase permissions");
        }
        Console.WriteLine("PASS phase-boundaries " + artifact);
    }
    string temporary = Path.Combine(Path.GetTempPath(), "nxp-adapter-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporary);
    try
    {
        var view = new TaskConfigView();
        int index = 0;
        foreach (var entry in fixtureResources)
        {
            string path = Path.Combine(temporary, (++index).ToString());
            File.WriteAllText(path, entry!["text"]!.GetValue<string>());
            string id = entry["id"]!.GetValue<string>(), format = entry["format"]!.GetValue<string>();
            if (id.StartsWith("config:", StringComparison.Ordinal)) view.AddConfig(id, path, format);
            else view.AddResource(id, path, format, sha256: entry["sha256"]?.GetValue<string>());
        }
        var context = TaskExecutionContext.Unknown("fixture-user", "fixture-script", "preview");
        if (fixture["queueFollowingWork"] is { } following)
            context = context with { Queue = new("queue", following.GetValue<string>(),
                fixture["queueNextTargetRelation"]?.GetValue<string>() ?? "unknown",
                fixture["queueNextLaunchOwner"]?.GetValue<string>() ?? "unknown") };
        if (fixture["executionContext"] is JsonObject launch)
            context = context with
            {
                LogSource = new(launch["logSourceKind"]?.GetValue<string>() ?? context.LogSource.Kind,
                    launch["logSourceAvailable"]?.GetValue<bool>() ?? context.LogSource.Available),
                Mode = launch["mode"]?.GetValue<string>() ?? context.Mode,
                LaunchOwner = launch["launchOwner"]?.GetValue<string>() ?? context.LaunchOwner,
                GameTarget = new("executable", launch["gameTarget"]?.GetValue<string>(), null,
                    launch["ready"]?.GetValue<bool>()),
                RuntimeActivity = launch["runtimeActivity"]?.GetValue<string>(),
                ConfigInputName = launch["configInputName"]?.GetValue<string>(),
                ConfigInputValue = launch["configInputValue"]?.GetValue<string>(),
            };
        if (fixture["admissionError"] is { } admissionError)
        {
            string expectedError = admissionError.GetValue<string>();
            Check(!string.IsNullOrWhiteSpace(expectedError), "negative admission requires an explicit error");
            bool rejected = false;
            var originalBytes = Directory.GetFiles(temporary).ToDictionary(path => path, File.ReadAllBytes);
            try
            {
                await TaskDiscoveryService.DiscoverAsync(protocol, view, manifest["name"]?.GetValue<string>() ?? artifact,
                    manifest["version"]!.GetValue<string>(), "fixture-user", "fixture-script", "zh-CN", true, default, context);
            }
            catch (InvalidDataException ex) when (ex.Message.Contains(expectedError, StringComparison.Ordinal)) { rejected = true; }
            Check(rejected, "expected admission rejection: " + expectedError);
            foreach (var original in originalBytes) Check(File.ReadAllBytes(original.Key).AsSpan().SequenceEqual(original.Value), "admission preserves configuration bytes");
            if (!example) artifacts.Add(artifact);
            passed++; completedCaseIds.Add(Path.GetFileNameWithoutExtension(file));
            Console.WriteLine("PASS admission " + Path.GetFileName(file));
            continue;
        }
        var plan = await TaskDiscoveryService.DiscoverAsync(protocol, view, manifest["name"]?.GetValue<string>() ?? artifact,
            manifest["version"]!.GetValue<string>(), "fixture-user", "fixture-script", "zh-CN", true, default, context);
        Check(plan.Coverage == fixture["coverage"]!.GetValue<string>(), $"discovery coverage: expected {fixture["coverage"]}, actual {plan.Coverage}");
        if (fixture["configChecks"] is JsonArray expectedChecks)
            foreach (var expected in expectedChecks)
            {
                var check = plan.ConfigAssessment!.Checks.Single(c => c.RuleId == expected!["ruleId"]!.GetValue<string>());
                Check(check.Evaluation == expected!["evaluation"]!.GetValue<string>(), "config evaluation " + check.RuleId);
                Check(check.ExecutionEffect == expected["executionEffect"]!.GetValue<string>(), "config effect " + check.RuleId);
                if (expected["locationCount"] is { } count)
                    Check(check.Locations.Count == count.GetValue<int>(), "config locations " + check.RuleId);
            }
        if (fixture["forbiddenPlanText"] is JsonArray forbidden)
            foreach (var value in forbidden)
                Check(!TaskProtocolJson.Write(plan).Contains(value!.GetValue<string>(), StringComparison.Ordinal), "private config excluded from plan");
        if (fixture["behaviorVariants"] is JsonArray variants)
            foreach (var variant in variants)
            {
                var changedView = new TaskConfigView();
                foreach (var entry in variant!["resources"]!.AsArray())
                {
                    string path = Path.Combine(temporary, (++index).ToString());
                    File.WriteAllText(path, entry!["text"]!.GetValue<string>());
                    changedView.AddConfig(entry["id"]!.GetValue<string>(), path, entry["format"]!.GetValue<string>());
                }
                foreach (var entry in fixtureResources.Where(r => !r["id"]!.GetValue<string>().StartsWith("config:")))
                {
                    string path = Path.Combine(temporary, (++index).ToString());
                    File.WriteAllText(path, entry!["text"]!.GetValue<string>());
                    changedView.AddResource(entry["id"]!.GetValue<string>(), path, entry["format"]!.GetValue<string>(), sha256: entry["sha256"]?.GetValue<string>());
                }
                var changedPlan = await TaskDiscoveryService.DiscoverAsync(protocol, changedView, manifest["name"]!.GetValue<string>(),
                    manifest["version"]!.GetValue<string>(), "fixture-user", "fixture-script", "zh-CN", true, default, context);
                Check((changedPlan.BehaviorSignature == plan.BehaviorSignature) == variant["sameBehavior"]!.GetValue<bool>(), "template-only behavior projection");
            }
        foreach (var item in fixture["tasks"]!.AsObject())
        {
            var task = plan.Tasks.Single(t => t.SourceKey == item.Key);
            Check(task.Enabled == item.Value!["enabled"]!.GetValue<bool>(), "selection " + item.Key);
            if (item.Value["detection"] is {} detection) Check(task.Detection == detection.GetValue<string>(), "detection " + item.Key);
            if (item.Value["role"] is {} role) Check(task.Role == role.GetValue<string>(), "role " + item.Key);
            if (item.Value["order"] is {} expectedOrder) Check(task.Order == expectedOrder.GetValue<int>(), "task order " + item.Key);
        }
        Check(plan.Tasks.Length == fixture["tasks"]!.AsObject().Count, "task count");
        if (fixture["diagnostics"] is JsonArray expectedDiagnostics)
            foreach (var expected in expectedDiagnostics)
            {
                var diagnostic = plan.Diagnostics.Single(d => d.Code == expected!["code"]!.GetValue<string>());
                string? sourceKey = diagnostic.TaskId is null ? null : plan.Tasks.Single(t => t.Id == diagnostic.TaskId).SourceKey;
                Check(sourceKey == expected!["task"]?.GetValue<string>(), "diagnostic owner");
                foreach (string locale in new[] { "zh-CN", "en-US" })
                    if (expected[locale] is {} localized)
                        Check(TaskDisplaySnapshot.Resolve(diagnostic.ReasonText, plan.DisplaySnapshot, locale, diagnostic.Message) == localized.GetValue<string>(), "diagnostic localization " + locale);
            }
        if (fixture["displayNames"] is JsonObject displayNames)
            foreach (var locale in displayNames)
                foreach (var expected in locale.Value!.AsObject())
                {
                    var task = plan.Tasks.Single(t => t.SourceKey == expected.Key);
                    Check(TaskDisplaySnapshot.Resolve(task.NameText, plan.DisplaySnapshot, locale.Key, task.Name)
                        == expected.Value!.GetValue<string>(), "localized task name " + locale.Key + ":" + expected.Key);
                }
        if (plan.Coverage != "unsupported")
        {
            var transaction = TaskSelectionTransaction.Freeze(Path.Combine(temporary, "journal"), view, plan.SelectionFields);
            var reducer = TaskRunReducer.CreateDaily("run", plan);
            var selected = plan.Tasks.Where(t => t.Enabled).Select(t => t.Id).ToArray();
            reducer.BeginAttempt("attempt", 1, selected);
            JsonObject? cursor = null;
            long sequence = 0;
            int observed = 0;
            foreach (var batchNode in fixture["batches"]!.AsArray())
            {
                observed++;
                var records = batchNode!["lines"]!.AsArray().Select(line => new TaskLogRecord(
                    batchNode["source"]?.GetValue<string>() ?? "stdout", batchNode["epoch"]?.GetValue<int>() ?? 0, ++sequence, line!.GetValue<string>())).ToArray();
                var batch = new TaskLogBatch(records, batchNode["gap"]?.GetValue<bool>() ?? false);
                var observation = await TaskProtocolScriptRunner.ExecuteAsync<TaskObservationBatch>(protocol.ObserveScript,
                    new { protocolVersion = protocol.Version, phase = "observe", runId = "run", attemptId = "attempt", attemptNumber = 1,
                        originalPlan = plan, attemptTaskIds = selected, adapterState = cursor,
                        acceptedState = reducer.AcceptedResults.ToDictionary(r => r.TaskId, r => new { r.Status, r.ExecutionOrdinal }),
                        logBatch = batch, isFinalCall = batchNode["final"]?.GetValue<bool>() ?? false,
                        terminationReason = batchNode["terminationReason"]?.GetValue<string>() ?? "none" }, view.ReadConfig, view.ReadResource, false, default);
                reducer.Accept(observation, batch);
                // Replay must be idempotent, using the identical output and evidence.
                reducer.Accept(observation, batch);
                cursor = observation.CursorState;
                if (batchNode["boundary"] is {} boundary) Check(reducer.RunBoundary == boundary.GetValue<string>(), "batch boundary");
                if (batchNode["incidentCount"] is {} incidentCount) Check(reducer.IncidentHistory.Length == incidentCount.GetValue<int>(), "batch incident count");
                if (batchNode["states"] is JsonObject expectedStates)
                    foreach (var item in expectedStates)
                        Check(reducer.Results.Single(r => r.TaskId == plan.Tasks.Single(t => t.SourceKey == item.Key).Id).Status == item.Value!.GetValue<string>(), "intermediate state " + item.Key);
            }
            if (observeCount != 0)
            {
                Check(observed <= observeCount, "fixture exceeds finite observation count");
                string accepted = TaskProtocolJson.Write(reducer.AcceptedResults);
                string? boundaryBeforeIdle = reducer.RunBoundary;
                while (observed++ < observeCount)
                {
                    var idleBatch = new TaskLogBatch([], false);
                    var idleObservation = await TaskProtocolScriptRunner.ExecuteAsync<TaskObservationBatch>(protocol.ObserveScript,
                        new { protocolVersion = protocol.Version, phase = "observe", runId = "run", attemptId = "attempt", attemptNumber = 1,
                            originalPlan = plan, attemptTaskIds = selected, adapterState = cursor,
                            acceptedState = reducer.AcceptedResults.ToDictionary(r => r.TaskId, r => new { r.Status, r.ExecutionOrdinal }),
                            logBatch = idleBatch, isFinalCall = false, terminationReason = "none" }, view.ReadConfig, view.ReadResource, false, default);
                    reducer.Accept(idleObservation, idleBatch); cursor = idleObservation.CursorState;
                    Check(TaskProtocolJson.Write(reducer.AcceptedResults) == accepted && reducer.RunBoundary == boundaryBeforeIdle,
                        "idle observation cannot fabricate or erase evidence");
                }
                var fresh = TaskRunReducer.CreateDaily("isolated-run", plan);
                fresh.BeginAttempt("isolated-attempt", 1, selected);
                string initial = TaskProtocolJson.Write(fresh.AcceptedResults);
                var empty = new TaskLogBatch([], false);
                var isolatedObservation = await TaskProtocolScriptRunner.ExecuteAsync<TaskObservationBatch>(protocol.ObserveScript,
                    new { protocolVersion = protocol.Version, phase = "observe", runId = "isolated-run", attemptId = "isolated-attempt", attemptNumber = 1,
                        originalPlan = plan, attemptTaskIds = selected, adapterState = (JsonObject?)null,
                        acceptedState = fresh.AcceptedResults.ToDictionary(r => r.TaskId, r => new { r.Status, r.ExecutionOrdinal }),
                        logBatch = empty, isFinalCall = false, terminationReason = "none" }, view.ReadConfig, view.ReadResource, false, default);
                fresh.Accept(isolatedObservation, empty);
                Check(TaskProtocolJson.Write(fresh.AcceptedResults) == initial, "new run must not inherit prior evidence");
            }
            if (fixture["boundary"] is {} expectedBoundary) Check(reducer.RunBoundary == expectedBoundary.GetValue<string>(), "run boundary");
            string lifecycle = fixture["lifecycle"]?.GetValue<string>() ?? "completed";
            reducer.FinishAttempt(lifecycle);
            foreach (var expected in fixture["results"]!.AsObject())
            {
                string id = plan.Tasks.Single(t => t.SourceKey == expected.Key).Id;
                string actual = reducer.Results.Single(t => t.TaskId == id).Status;
                Check(actual == expected.Value!.GetValue<string>(), $"result {expected.Key}: expected {expected.Value}, actual {actual}");
            }
            if (fixture["incidents"] is JsonArray expectedIncidents)
            {
                var actual = reducer.IncidentHistory;
                Check(actual.Length == expectedIncidents.Count, "incident history length");
                for (int i = 0; i < actual.Length; i++)
                {
                    var expected = expectedIncidents[i]!;
                    var incident = actual[i].Incident;
                    Check(incident.Resolution == expected["resolution"]!.GetValue<string>(), "incident resolution " + i);
                    string? key = incident.TaskId is null ? null : plan.Tasks.Single(t => t.Id == incident.TaskId).SourceKey;
                    Check(key == expected["task"]?.GetValue<string>(), "incident owner " + i);
                    if (expected["reason"] is {} reason)
                        Check(TaskDisplaySnapshot.Resolve(incident.ReasonText, protocol.Localization, "zh-CN", incident.ReasonCode) == reason.GetValue<string>(), "incident reason " + i);
                }
            }
            var retry = await TaskProtocolScriptRunner.ExecuteAsync<JsonObject>(protocol.RetryScript,
                new { protocolVersion = protocol.Version, phase = "retry", originalPlan = plan, attemptsUsed = 1, maxAttempts = 2,
                    taskStates = reducer.Results.ToDictionary(r => r.TaskId, r => r.Status), cancelled = lifecycle == "cancelled", budgetExhausted = false,
                    configResources = view.ConfigResources, executionContext = context }, view.ReadConfig, view.ReadResource, false, default);
            var safe = reducer.SelectRetry(2, lifecycle == "cancelled", false);
            string decision = retry["decision"]!.GetValue<string>();
            if (decision == "native_resume")
            {
                Check(protocol.Version == "0.2.0" && safe.Decision == "selective", "native retry qualification");
                Check(plan.Tasks.Where(t => t.Enabled).All(t => t.RetryPolicy?.Mode == "native_resume"), "native retry declaration");
                Check(retry["filePatches"]!.AsArray().Count == 0, "native retry preserves configuration");
                Check(retry["launchScopeTaskIds"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(selected), "native full launch scope");
                Check(retry["targetTaskIds"]!.AsArray().Select(n => n!.GetValue<string>()).Order().SequenceEqual(reducer.RetryTargets.Order()), "native failed targets");
                view.VerifyUnchanged();
            }
            else Check(decision == safe.Decision, "retry decision");
            if (decision == "selective")
            {
                Check(retry["includedTaskIds"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(safe.IncludedTaskIds), "retry scope");
                var before = view.ConfigResources.ToDictionary(r => r.Id, r => view.Snapshot(r.Id));
                transaction.Apply(view, TaskProtocolJson.Read<TaskConfigPatch[]>(retry["filePatches"]!.ToJsonString()));
                if (fixture["retryDocuments"] is JsonObject expectedDocuments)
                    foreach (var (resourceId, expectedDocument) in expectedDocuments)
                    {
                        var snapshot = before[resourceId];
                        Check(JsonNode.DeepEquals(expectedDocument,
                            new TaskConfigDocument(File.ReadAllBytes(snapshot.Path), snapshot.Format).Document),
                            "retry document preserves unrelated controller/account/option fields: " + resourceId);
                    }
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
        completedCaseIds.Add(Path.GetFileNameWithoutExtension(file));
        Console.WriteLine("PASS " + Path.GetFileName(file));
    }
    catch (Exception ex)
    {
        failures.Add(new { caseId = Path.GetFileNameWithoutExtension(file), error = ex.ToString(), evidenceDirectory = temporary });
        Console.Error.WriteLine("FAIL " + Path.GetFileName(file) + ": " + ex.Message + "; evidence retained: " + temporary);
    }
}
if (failures.Count == 0 && !artifacts.SetEquals(selectedMode ? [selectedArtifact!] : supportedArtifacts))
    throw new InvalidDataException("Selected production adapters did not all execute");
Console.WriteLine($"Task protocol: {passed} passed, {failures.Count} failed, 0 skipped; {artifacts.Count} production adapters through Host Jint/reducer/config journal.");
string[] editorCaseIds = observeCount == 12 ? await FiniteEditor.RunAsync(root, selectedArtifact!) : [];
if (reportPath is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    File.WriteAllText(reportPath, System.Text.Json.JsonSerializer.Serialize(new
    {
        schemaVersion = 1, evidenceType = "actual", artifact = selectedArtifact,
        expectedCaseIds = files.Select(Path.GetFileNameWithoutExtension).ToArray(),
        completedCaseIds,
        passed, failed = failures.Count, failures, skipped = 0, observeCount, isolatedRunChecked = observeCount != 0, editorCaseIds,
        real = new[] { "production Jint", "discovery", "reducer", "configuration journal" },
        substituted = new[] { "repository synthetic configuration and log fixtures" },
    }));
}
if (failures.Count != 0) Environment.ExitCode = 1;
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
                var reducer = TaskRunReducer.CreateDaily("replay", plan);
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
                        new { protocolVersion = protocol.Version, phase = "observe", runId = "replay", attemptId = "attempt", attemptNumber = 1,
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
