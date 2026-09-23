using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Platform.Storage;

// Exercises real exchange/store/journal code with synthetic adapter configurations.
// No target application, user directory, or process is launched or inspected.
internal static class AccountIsolationProbe
{
    internal static async Task RunAsync(string plugins, string output)
    {
        string[] fixtures = ["bettergi-safe-retry", "march7th-completed-reward", "baah-reward-safe-retry",
            "zzz-safe-retry", "maaend-callbacks", "maastellasora-callbacks",
            "r2-oknte-exclusive-order", "r2-okww-farm-tacet"];
        var results = new JsonArray();
        ConfigSwapSession.ConfigureRecovery(_ => null, () => []);
        foreach (string fixtureName in fixtures)
        {
            var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(plugins, "tools", "task-protocol", "fixtures", fixtureName + ".json")))!;
            string artifact = fixture["artifact"]!.GetValue<string>();
            string plugin = Path.Combine(plugins, "plugins", "specialized", artifact);
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(plugin, "plugin.json")))!.AsObject();
            var protocol = TaskProtocolManifest.Freeze(manifest, plugin)!;
            foreach (string mode in new[] { "sync", "no-sync", "retry", "cancelled", "unconfirmed-cleanup-recovery", "interrupted-retry" })
            {
                string script = "account-probe-" + Guid.NewGuid().ToString("N");
                string temp = Path.Combine(Path.GetTempPath(), script);
                string site = Path.Combine(temp, "config"), resources = Path.Combine(temp, "resources");
                Directory.CreateDirectory(site); Directory.CreateDirectory(resources);
                bool passed = false;
                try
                {
                    var entries = TaskFixtureResources.Read(fixture, Path.Combine(plugins, "tools", "task-protocol", "fixtures")).Select((r, i) =>
                        (Id: r!["id"]!.GetValue<string>(), Format: r["format"]!.GetValue<string>(),
                         Text: r["text"]!.GetValue<string>(), Sha256: r["sha256"]?.GetValue<string>(), File: i + ".config")).ToArray();
                    foreach (var r in entries) File.WriteAllText(Path.Combine(r.Id.StartsWith("config:") ? site : resources, r.File), r.Text);
                    File.WriteAllText(Path.Combine(site, "business-counter.json"), "{\"count\":0}");
                    var original = Snapshot(site);
                    TaskConfigView View(string directory)
                    {
                        var view = new TaskConfigView();
                        foreach (var r in entries)
                            if (r.Id.StartsWith("config:")) view.AddConfig(r.Id, Path.Combine(directory, r.File), r.Format);
                            else view.AddResource(r.Id, Path.Combine(resources, r.File), r.Format, sha256: r.Sha256);
                        return view;
                    }
                    Task<TaskPlan> Discover(string user) => TaskDiscoveryService.DiscoverAsync(protocol, View(site),
                        manifest["name"]!.GetValue<string>(), manifest["version"]!.GetValue<string>(), user, script, "zh-CN", false, default);
                    // Seed independent snapshots through the same implicit-adoption path as production.
                    var seedA = new ConfigRunSession(script, "A", site, false);
                    Require(seedA.Prepare(out var errorA), errorA ?? "seed A");
                    Require(seedA.FinalizeRun(false) is null, "seed A restore");
                    // Every input fixture has JSON syntax, including those read through the YAML codec.
                    var firstConfig = entries.First(r => r.Id.StartsWith("config:"));
                    string variedPath = Path.Combine(site, firstConfig.File);
                    var varied = JsonNode.Parse(File.ReadAllText(variedPath))!;
                    if (artifact == "OkWutheringWaves") varied["Additional Tasks to Run After Daily Task"] = new JsonArray();
                    else Require(ToggleFirstBoolean(varied), "fixture must have a selection to vary");
                    File.WriteAllText(variedPath, varied.ToJsonString());
                    var seedB = new ConfigRunSession(script, "B", site, false);
                    Require(seedB.Prepare(out var errorB), errorB ?? "seed B");
                    Require(seedB.FinalizeRun(false) is null, "seed B restore");
                    // Restore the external pre-run installation configuration, distinct from both users' stores.
                    foreach (var r in entries.Where(r => r.Id.StartsWith("config:"))) File.WriteAllText(Path.Combine(site, r.File), r.Text);
                    var aInitial = Snapshot(ConfigPaths.StoreDir(script, "A"));
                    var bInitial = Snapshot(ConfigPaths.StoreDir(script, "B"));
                    Require(!Same(aInitial, bInitial), "accounts have different persisted configurations");
                    TaskPlan? planA = null;
                    foreach (string user in new[] { "A", "B" })
                    {
                        string other = user == "A" ? "B" : "A";
                        var otherBefore = Snapshot(ConfigPaths.UserDir(script, other));
                        var ownBefore = Snapshot(ConfigPaths.StoreDir(script, user));
                        var ownDocuments = entries.Where(r => r.Id.StartsWith("config:")).ToDictionary(r => r.File,
                            r => new TaskConfigDocument(File.ReadAllBytes(Path.Combine(ConfigPaths.StoreDir(script, user), r.File)), r.Format).Document);
                        var session = new ConfigRunSession(script, user, site, false);
                        Require(session.Prepare(out var error), error ?? "prepare");
                        Require(Same(Snapshot(site), ownBefore), "only selected account snapshot enters site");
                        var plan = await Discover(user);
                        Require(plan.Coverage != "unsupported", "supported fixture");
                        if (user == "A") planA = plan;
                        else
                        {
                            Require(plan.Tasks.Select(t => t.Name).SequenceEqual(planA!.Tasks.Select(t => t.Name)), "same labels across accounts");
                            Require(!plan.Tasks.Select(t => t.Enabled).SequenceEqual(planA.Tasks.Select(t => t.Enabled)), "different effective selections");
                        }
                        string journal = Path.Combine(ConfigPaths.WorkDir(script, user), "task-selection");
                        var view = View(site);
                        var transaction = TaskSelectionTransaction.Freeze(journal, view, plan.SelectionFields,
                            owner: ConfigSessionMark.TryRead(script, user));
                        string retryDecision = "not-requested";
                        if (mode is "retry" or "cancelled" or "interrupted-retry")
                        {
                            var retry = await TaskProtocolScriptRunner.ExecuteAsync<JsonObject>(protocol.RetryScript,
                                new { protocolVersion = protocol.Version, phase = "retry", originalPlan = plan,
                                    attemptsUsed = 1, maxAttempts = 2, cancelled = mode == "cancelled", budgetExhausted = false,
                                    taskStates = plan.Tasks.ToDictionary(t => t.Id, t => t.Enabled ? "failed" : "disabled"), configResources = view.ConfigResources },
                                view.ReadConfig, view.ReadResource, false, default);
                            retryDecision = retry["decision"]!.GetValue<string>();
                            if (mode == "cancelled") Require(retryDecision == "stop", "cancelled run cannot restart");
                            if (retryDecision == "selective")
                            {
                                transaction.Apply(view, TaskProtocolJson.Read<TaskConfigPatch[]>(retry["filePatches"]!.ToJsonString()));
                                var beforeRetry = Snapshot(site);
                                Require(session.PrepareForRetry() is null, "retry preparation");
                                Require(Same(beforeRetry, Snapshot(site)), "retry keeps active selective configuration");
                            }
                            else Require(retryDecision == "stop", "explicit safe retry decision");
                        }
                        File.WriteAllText(Path.Combine(site, "business-counter.json"), user == "A" ? "{\"count\":11}" : "{\"count\":22}");
                        if (mode is "unconfirmed-cleanup-recovery" or "interrupted-retry")
                        {
                            session.MarkProcessCleanupUnconfirmed("synthetic lifecycle fault; no process was started");
                            Require(session.FinalizeRun(true) is not null, "cleanup uncertainty blocks finalization");
                            Require(Directory.Exists(journal) && Directory.Exists(ConfigPaths.CacheDir(script, user)), "recovery evidence retained");
                            Require(Same(ownBefore, Snapshot(ConfigPaths.StoreDir(script, user))), "blocked finalization cannot sync");
                            // New recovery object, persisted journal only: do not call the old transaction to recover.
                            ConfigSwapSession.ConfigureRecovery(_ => null, () => []);
                            ConfigSwapSession.RecoverIfNeeded(script, user, site);
                        }
                        else
                        {
                            session.RestoreTaskSelections = () => { transaction.Restore(); transaction.Complete(); return null; };
                            Require(session.FinalizeRun(mode != "no-sync") is null, "finalize");
                            Require(session.FinalizeRun(mode != "no-sync") is null, "idempotent finalize");
                        }
                        Require(Same(original, Snapshot(site)), "external installation configuration restored byte for byte");
                        Require(Same(otherBefore, Snapshot(ConfigPaths.UserDir(script, other))), "other account including metadata untouched");
                        var expected = ownBefore.ToDictionary(p => p.Key, p => p.Value);
                        if (mode is "sync" or "retry" or "cancelled") expected["business-counter.json"] = Hash(user == "A" ? "{\"count\":11}" : "{\"count\":22}");
                        // Selection array spans may be reserialized by the approved codec. Verify the complete
                        // configuration tree, while external site and the other account retain byte checks.
                        foreach (var r in entries.Where(r => r.Id.StartsWith("config:")))
                        {
                            string stored = Path.Combine(ConfigPaths.StoreDir(script, user), r.File);
                            Require(JsonNode.DeepEquals(ownDocuments[r.File], new TaskConfigDocument(File.ReadAllBytes(stored), r.Format).Document), "own complete configuration restored");
                            expected[r.File] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(stored)));
                        }
                        Require(Same(expected, Snapshot(ConfigPaths.StoreDir(script, user))), "own selection restored, business updates follow sync policy");
                        Require(!Directory.Exists(journal), "selection journal completed");
                        Require(ConfigSessionMark.TryRead(script, user) is null, "exchange journal completed");
                        results.Add(new JsonObject { ["artifact"] = artifact, ["mode"] = mode, ["user"] = user, ["retryDecision"] = retryDecision, ["status"] = "PASS" });
                    }
                    passed = true;
                    Console.WriteLine($"PASS account isolation {artifact} {mode} A/B");
                }
                finally
                {
                    if (passed)
                    {
                        Directory.Delete(temp, true);
                        Directory.Delete(Path.Combine(AppPaths.DataDir, script), true);
                    }
                    else Console.Error.WriteLine("Probe failed; retained isolated evidence: " + temp + " ; " + Path.Combine(AppPaths.DataDir, script));
                }
            }
        }
        File.WriteAllText(output, new JsonObject { ["scope"] = "Synthetic configs through Host exchange, Jint discovery/retry, store and persisted recovery; no game process or full qualification", ["cases"] = results }.ToJsonString(new() { WriteIndented = true }));
        Console.WriteLine($"Account isolation: {results.Count} passed, 0 skipped; eight production adapters.");
    }

    private static bool ToggleFirstBoolean(JsonNode node)
    {
        if (node is JsonObject obj)
            foreach (var p in obj.ToArray())
            {
                if (p.Value is JsonValue v && v.TryGetValue<bool>(out bool flag)) { obj[p.Key] = !flag; return true; }
                if (p.Value is not null && ToggleFirstBoolean(p.Value)) return true;
            }
        if (node is JsonArray arr)
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i] is JsonValue v && v.TryGetValue<bool>(out bool flag)) { arr[i] = !flag; return true; }
                if (arr[i] is {} child && ToggleFirstBoolean(child)) return true;
            }
        return false;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private static Dictionary<string, string> Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(root, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
    private static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var h) && h == p.Value);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException("Account isolation: " + message); }
}
