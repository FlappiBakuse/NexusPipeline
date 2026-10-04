using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Editing;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Plugins.DataSpecialized;

internal static class ConfigRepairProbe
{
    internal static void Run(string root, string report)
    {
        if (File.Exists(report)) throw new IOException("Report already exists");
        string[] artifacts = ["BAAH", "BetterGI", "March7thAssistant", "ZenlessZoneZeroOneDragon", "MaaEnd", "MaaStellaSora", "OkNTE", "OkWutheringWaves"];
        var results = new List<object>();
        int failed = 0;
        foreach (string artifact in artifacts)
        {
            string directory = Path.Combine(root, "plugins", "specialized", artifact);
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "plugin.json")))!.AsObject();
            if (!TaskProtocolManifest.TryValidate(manifest, out var error)) throw new InvalidDataException(error);
            var editor = new ConfigEditorDescriptor(artifact, directory, "data/editor.js", File.ReadAllText(Path.Combine(directory, "data", "editor.js")));
            var protocol = TaskProtocolManifest.Freeze(manifest, directory)!;
            if (protocol.RepairRules.Length == 0) throw new InvalidDataException("Missing repair rules: " + artifact);
            foreach (var rule in protocol.RepairRules)
            {
                try
                {
                    var input = new JsonObject { ["untouched"] = "keep", ["budget"] = 7 };
                    JsonNode? before = rule.Kind switch
                    {
                        "bind_game_path" => JsonValue.Create(@"C:\Old\game.exe"),
                        "normalize_enum" => JsonValue.Create("Shutdown"),
                        "replace_enum" => JsonValue.Create(rule.FromValues[0]),
                        "enable_boolean" => JsonValue.Create(false),
                        "mfa_tasks" => new JsonArray(new JsonObject { ["entry"] = "Daily", ["default_check"] = true }, new JsonObject { ["entry"] = "ComputerOperationAction", ["default_check"] = true }),
                        "mxu_tasks" => new JsonArray(new JsonObject { ["id"] = "chosen", ["tasks"] = new JsonArray(new JsonObject { ["id"] = "daily", ["taskName"] = "Daily", ["enabled"] = true }, new JsonObject { ["id"] = "power", ["taskName"] = "__MXU_POWER__", ["enabled"] = true }) }, new JsonObject { ["id"] = "other", ["tasks"] = new JsonArray() }),
                        "mxu_preactions" => new JsonArray(new JsonObject { ["id"] = "chosen", ["tasks"] = new JsonArray() }, new JsonObject { ["id"] = "other", ["preActions"] = new JsonArray(new JsonObject { ["id"] = "other-launch", ["program"] = "other.exe" }) }),
                        _ => throw new InvalidDataException("Unknown repair kind")
                    };
                    var parent = input;
                    foreach (var segment in rule.Selector.Take(rule.Selector.Count - 1))
                    {
                        var child = new JsonObject(); parent[segment!.GetValue<string>()] = child; parent = child;
                    }
                    parent[rule.Selector.Last()!.GetValue<string>()] = before;
                    if (rule.Kind is "mxu_tasks" or "mxu_preactions") input["settings"] = new JsonObject { ["autoStartInstanceId"] = "chosen" };
                    string text = rule.Format == "json" ? input.ToJsonString() : string.Join("\n", input.Select(p => p.Key + ": " + p.Value!.ToJsonString())) + "\n";
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    var context = new ConfigRepairContext(@"C:\Games\game.exe", "--fixture", true);
                    var proposal = ConfigRepairPolicy.Propose(rule, artifact, "fixture", "account-a", "script", "profile", "locator", 1, bytes, context, out var patched, editor);
                    Check(proposal?.Available == true && patched is not null, "proposal");
                    var doc = new TaskConfigDocument(patched!, rule.Format);
                    Check(doc.Document!["untouched"]!.GetValue<string>() == "keep" && doc.Document["budget"]!.GetValue<int>() == 7, "unrelated fields");
                    if (rule.Kind is "mxu_tasks" or "mfa_tasks")
                    {
                        bool mxu = rule.Kind == "mxu_tasks";
                        var tasks = mxu ? doc.Document["instances"]![0]!["tasks"]! : doc.Document["TaskItems"]!;
                        Check(tasks.AsArray().Count == 2 && JsonNode.DeepEquals(tasks[0], mxu ? input["instances"]![0]!["tasks"]![0] : input["TaskItems"]![0]), "daily task unchanged");
                        Check(!tasks[1]![mxu ? "enabled" : "default_check"]!.GetValue<bool>(), "power disabled");
                        if (mxu) Check(JsonNode.DeepEquals(input["instances"]![1], doc.Document["instances"]![1]), "other instance");
                    }
                    else if (rule.Kind == "mxu_preactions")
                    {
                        var action = doc.Document["instances"]![0]!["preActions"]![0]!;
                        Check(action["enabled"]!.GetValue<bool>() && !action["waitForExit"]!.GetValue<bool>(), "pre-connection launch");
                        Check(action["program"]!.GetValue<string>() == context.GamePath && action["args"]!.GetValue<string>() == context.GameArguments, "bound launcher");
                        Check(JsonNode.DeepEquals(input["instances"]![1], doc.Document["instances"]![1]), "other instance");
                        Check(JsonNode.DeepEquals(input["instances"]![0]!["tasks"], doc.Document["instances"]![0]!["tasks"]), "native tasks unchanged");
                        var existing = (JsonObject)input.DeepClone();
                        existing["instances"]![0]!["preActions"] = new JsonArray(new JsonObject { ["id"] = "existing", ["program"] = "old.exe", ["args"] = "--keep", ["enabled"] = false, ["waitForExit"] = true },
                            new JsonObject { ["id"] = "utility", ["program"] = "utility.exe", ["args"] = "--utility", ["enabled"] = true, ["waitForExit"] = true });
                        Check(ConfigRepairPolicy.Propose(rule, artifact, "fixture", "account-a", "script", "profile", "locator", 1,
                            Encoding.UTF8.GetBytes(existing.ToJsonString()), context, out var updated, editor)?.Available == true, "existing prelaunch repair");
                        var updatedActions = JsonNode.Parse(updated!)!["instances"]![0]!["preActions"]!;
                        Check(updatedActions[0]!["args"]!.GetValue<string>() == "--keep" && updatedActions[0]!["id"]!.GetValue<string>() == "existing", "arguments and identity retained");
                        Check(JsonNode.DeepEquals(existing["instances"]![0]!["preActions"]![1], updatedActions[1]), "subsequent program retained");
                        var legacy = (JsonObject)input.DeepClone();
                        legacy["instances"]![0]!["preAction"] = new JsonObject { ["program"] = "old.exe", ["args"] = "--legacy", ["enabled"] = false, ["waitForExit"] = true };
                        Check(ConfigRepairPolicy.Propose(rule, artifact, "fixture", "account-a", "script", "profile", "locator", 1,
                            Encoding.UTF8.GetBytes(legacy.ToJsonString()), context, out var legacyPatched, editor)?.Available == true, "legacy preaction repair");
                        Check(JsonNode.Parse(legacyPatched!)!["instances"]![0]!["preActions"]![0]!["args"]!.GetValue<string>() == "--legacy", "legacy arguments retained");
                        Check(JsonNode.DeepEquals(JsonNode.Parse(legacyPatched!)!["instances"]![0]!["preAction"], legacy["instances"]![0]!["preAction"]), "legacy field retained");
                    }
                    else if (rule.Kind == "enable_boolean") Check(doc.ReadSelection(rule.Selector)!.GetValue<bool>(), "logging enabled");
                    else Check(doc.ReadSelection(rule.Selector)!.GetValue<string>() == (rule.Kind == "bind_game_path" ? context.GamePath : rule.ToValue), "repaired value");
                    Check(ConfigRepairPolicy.Propose(rule, artifact, "fixture", "account-a", "script", "profile", "locator", 2, patched!, context, out _, editor) is null, "idempotence");
                    if (rule.Kind == "enable_boolean")
                    {
                        foreach (string source in new[] { "{}", "{\"SAVE_LOG_TO_FILE\":false}" })
                        {
                            Check(ConfigRepairPolicy.Propose(rule, artifact, "fixture", "account-a", "script", "profile", "locator", 1,
                                Encoding.UTF8.GetBytes(source), context, out var result, editor)?.Available == true, "missing/disabled logging");
                            Check(JsonNode.Parse(result!)!["SAVE_LOG_TO_FILE"]!.GetValue<bool>(), "logging persisted");
                        }
                        foreach (string source in new[] { "{\"SAVE_LOG_TO_FILE\":true}", "{\"SAVE_LOG_TO_FILE\":\"false\"}", "{\"SAVE_LOG_TO_FILE\":null}" })
                            Check(ConfigRepairPolicy.Propose(rule, artifact, "fixture", "account-a", "script", "profile", "locator", 1,
                                Encoding.UTF8.GetBytes(source), context, out _, editor) is null, "safe or malformed logging unchanged");
                    }
                    if (rule.ResourceId == "config:instances/{instance}.json")
                    {
                        string field = rule.Selector[0]!.GetValue<string>();
                        var shared = new JsonObject { ["Instance.chosen." + field] = before!.DeepClone(),
                            ["Instance.default." + field] = field == "TaskItems" ? new JsonArray() : JsonValue.Create("None") };
                        string originalShared = shared.ToJsonString();
                        var inheritedContext = context with { ConfigInputValue = "chosen" };
                        JsonNode? Read(string id) => id switch
                        {
                            "extra:0" => new JsonObject { ["DefaultConfig"] = "profile" },
                            "config:mfa_profile.json" => shared,
                            _ => null,
                        };
                        Check(ConfigRepairPolicy.Propose(rule, artifact, "fixture", "account-a", "script", "profile", "locator", 1,
                            Encoding.UTF8.GetBytes("{\"untouched\":42}"), inheritedContext, out var inherited, editor, Read)?.Available == true,
                            "instance inheritance repair");
                        var local = JsonNode.Parse(inherited!)!;
                        Check(local["untouched"]!.GetValue<int>() == 42 && local[field] is not null, "local override only");
                        Check(originalShared == shared.ToJsonString(), "shared settings unchanged");
                        Check(ConfigRepairPolicy.Propose(rule, artifact, "fixture", "account-a", "script", "profile", "locator", 1,
                            inherited!, inheritedContext, out _, editor, Read) is null, "local override idempotence");
                    }
                    if (rule.SkipWhen is { } skip)
                    {
                        input[skip["selector"]![0]!.GetValue<string>()] = skip["equals"]!.DeepClone();
                        text = rule.Format == "json" ? input.ToJsonString() : string.Join("\n", input.Select(p => p.Key + ": " + p.Value!.ToJsonString())) + "\n";
                        Check(ConfigRepairPolicy.Propose(rule, artifact, "fixture", "account-a", "script", "profile", "locator", 1, Encoding.UTF8.GetBytes(text), context, out _, editor) is null, "skip condition");
                    }
                    results.Add(new { artifact, rule = rule.Id, status = "PASS" });
                    Console.WriteLine($"PASS {artifact}/{rule.Id}");
                }
                catch (Exception ex)
                {
                    failed++;
                    results.Add(new { artifact, rule = rule.Id, status = "FAIL", error = ex.ToString() });
                    Console.WriteLine($"FAIL {artifact}/{rule.Id}: {ex.Message}");
                }
            }
        }
        try
        {
            string directory = Path.Combine(root, "plugins", "specialized", "MaaStellaSora");
            var editor = new ConfigEditorDescriptor("maas", directory, "data/editor.js", File.ReadAllText(Path.Combine(directory, "data", "editor.js")));
            string temporary = Path.Combine(Path.GetTempPath(), "nxp-mfa-edit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            string extra = Path.Combine(temporary, "appsettings.json");
            const string original = "{\"NoAutoStart\":\"False\",\"GlobalStartEnabled\":\"True\",\"Other\":42}";
            File.WriteAllText(extra, original);
            var snapshots = new[] { new ConfigValidationExtraSnapshot(extra, temporary) { SingleFilePath = extra, AllowWrite = true } };
            var declaration = new NexusPipeline.Modules.Scripts.ScriptInstance { Id = "fixture", Name = "Fixture" };
            foreach (string action in new[] { "None", "StartupSoftware", "StartupSoftwareAndScript", "StartupScriptOnly" })
            {
                string main = Path.Combine(temporary, "instance.json");
                string initial = new JsonObject { ["BeforeTask"] = action, ["SoftwarePath"] = @"C:\Games\launcher.exe" }.ToJsonString();
                File.WriteAllText(main, initial);
                var prepared = ConfigEditScriptRunner.ExecuteAsync(editor, declaration, null, temporary, "config-edit-preparation", snapshots,
                    allowMainWrites: false, allowExtraWrites: true).GetAwaiter().GetResult();
                Check(prepared.Error == "" && JsonNode.Parse(File.ReadAllText(extra))!["NoAutoStart"]!.GetValue<string>() == "True", "edit suppresses every startup action");
                Check(File.ReadAllText(main) == initial, "configured startup action retained");
                var committed = ConfigEditScriptRunner.ExecuteAsync(editor, declaration, null, temporary, "config-edit-commit", snapshots,
                    allowMainWrites: false, allowExtraWrites: true).GetAwaiter().GetResult();
                Check(committed.Error == "" && File.ReadAllText(main) == initial, "commit retains startup path and action");
                var settings = JsonNode.Parse(File.ReadAllText(extra))!;
                Check(settings["NoAutoStart"]!.GetValue<string>() == "False" && settings["GlobalStartEnabled"]!.GetValue<string>() == "True" && settings["Other"]!.GetValue<int>() == 42, "commit clears one-shot suppression only");
            }
            results.Add(new { artifact = "MaaStellaSora", rule = "editor-lifecycle", status = "PASS", boundary = "actual Jint; native GUI NOT_RUN", directory = temporary });
            Console.WriteLine("PASS MaaStellaSora/editor-lifecycle");
        }
        catch (Exception ex)
        {
            failed++;
            results.Add(new { artifact = "MaaStellaSora", rule = "editor-lifecycle", status = "FAIL", error = ex.ToString() });
            Console.WriteLine("FAIL MaaStellaSora/editor-lifecycle: " + ex.Message);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { artifacts, total = results.Count, failed, results }, new JsonSerializerOptions { WriteIndented = true }));
        if (failed != 0) throw new InvalidDataException($"{failed} repair probes failed");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidDataException(message);
    }
}
