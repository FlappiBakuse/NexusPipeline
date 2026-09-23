using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Plugins.DataSpecialized;
using NexusPipeline.Modules.Scripts;

internal static class LegacyValidatorComparisonProbe
{
    internal static async Task RunAsync(string plugins, string inputsPath)
    {
        var inputs = JsonNode.Parse(File.ReadAllText(inputsPath))!;
        var results = new JsonArray();
        foreach (var item in inputs["scripts"]!.AsArray())
        {
            string artifact = item!["artifact"]!.GetValue<string>();
            var (fixture, rule) = artifact switch
            {
                "BetterGI" => ("bettergi-safe-retry", "bettergi.game_target"),
                "March7thAssistant" => ("march7th-completed-reward", "march7th.game_target"),
                "ZenlessZoneZeroOneDragon" => ("zzz-safe-retry", "zzz.game_target"),
                "BAAH" => ("baah-reward-safe-retry", "baah.game_target"),
                _ => throw new InvalidDataException("Unknown validator")
            };
            byte[] oldBytes = File.ReadAllBytes(item["path"]!.GetValue<string>());
            if (!Convert.ToHexString(SHA256.HashData(oldBytes)).Equals(item["sha256"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Legacy validator source changed");
            string package = Path.Combine(plugins, "plugins", "specialized", artifact);
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(package, "plugin.json")))!.AsObject();
            var protocol = TaskProtocolManifest.Freeze(manifest, package)!;
            string[] variants = artifact == "BAAH"
                ? ["match", "mismatch", "empty", "absent", "unbound", "mixed-case", "adb-default", "adb-number", "adb-mismatch", "log-off", "log-absent", "log-stdout", "adb-serial"]
                : artifact == "BetterGI" ? ["match", "mismatch", "empty", "absent", "unbound", "mixed-case", "directory"]
                : ["match", "mismatch", "empty", "absent", "unbound", "mixed-case", "yaml-escaped"];
            foreach (string variant in variants)
            {
                string root = Path.Combine(Path.GetTempPath(), "nxp-validator-compare-" + Guid.NewGuid().ToString("N"));
                string main = Path.Combine(root, "main"), extra = Path.Combine(root, "extra");
                Directory.CreateDirectory(main); Directory.CreateDirectory(extra);
                bool passed = false;
                try
                {
                    string target = Path.Combine(root, "game.exe"), other = Path.Combine(root, "other.exe");
                    File.WriteAllText(target, "inert"); File.WriteAllText(other, "inert");
                    string value = variant switch { "mismatch" => other, "empty" => "", "mixed-case" => target.ToUpperInvariant().Replace('\\', '/'), "directory" => root, "yaml-escaped" => target, _ => target.Replace('\\', '/') };
                    var view = new TaskConfigView();
                    var source = JsonNode.Parse(File.ReadAllText(Path.Combine(plugins, "tools", "task-protocol", "fixtures", fixture + ".json")))!;
                    var entries = source["resources"]!.AsArray().Select(e => e!.DeepClone()).ToList();
                    bool adb = variant.StartsWith("adb-", StringComparison.Ordinal);
                    foreach (var entry in entries.Where(e => e["id"]!.GetValue<string>().StartsWith("config:")))
                    {
                        var document = JsonNode.Parse(entry["text"]!.GetValue<string>())!.AsObject();
                        if (artifact == "March7thAssistant" && variant != "absent") document["game_path"] = value;
                        if (artifact == "BAAH")
                        {
                            if (variant != "absent") document["TARGET_EMULATOR_PATH"] = value;
                            if (adb && variant != "adb-default")
                            {
                                document["TARGET_IP_PATH"] = "127.0.0.1";
                                document["TARGET_PORT"] = variant == "adb-mismatch" ? 5556 : 5555;
                            }
                            if (variant == "adb-serial") { document["ADB_DIRECT_USE_SERIAL_NUMBER"] = true; document["ADB_SEIAL_NUMBER"] = "emulator-5554"; }
                        }
                        entry["text"] = Encode(document, entry["format"]!.GetValue<string>());
                    }
                    if (artifact == "ZenlessZoneZeroOneDragon")
                        entries.Add(new JsonObject { ["id"] = "config:game_account.yml", ["format"] = "yaml", ["text"] = variant == "absent" ? "unrelated: true" : "game_path: " + JsonValue.Create(value)!.ToJsonString() });
                    string? extraId = artifact switch { "BetterGI" => "extra-user-config", "BAAH" => "software-config", _ => null };
                    if (extraId is not null)
                    {
                        JsonObject document = artifact == "BetterGI" ? new() { ["genshinStartConfig"] = variant == "absent" ? new JsonObject() : new JsonObject { ["installPath"] = value } }
                            : new() { ["SAVE_LOG_TO_FILE"] = variant is not ("log-off" or "log-stdout"), ["TARGET_EMULATOR_PATH"] = other };
                        if (variant == "log-absent") document.Remove("SAVE_LOG_TO_FILE");
                        entries.Add(new JsonObject { ["id"] = extraId, ["format"] = "json", ["text"] = document.ToJsonString() });
                    }
                    int counter = 0;
                    foreach (var entry in entries)
                    {
                        string id = entry["id"]!.GetValue<string>(), format = entry["format"]!.GetValue<string>();
                        bool config = id.StartsWith("config:", StringComparison.Ordinal);
                        string path = config ? Path.Combine(main, id[7..]) : Path.Combine(extra, (++counter) + ".json");
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, entry["text"]!.GetValue<string>());
                        if (config) view.AddConfig(id, path, format); else view.AddResource(id, path, format);
                    }
                    var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
                    string bound = variant == "unbound" ? "" : adb ? "127.0.0.1:5555" : target;
                    var script = new ScriptInstance { Id = "fixture", Name = artifact, PluginType = manifest["name"]!.GetValue<string>(), RootPath = root, ConfigPath = main, GameExe = bound, GameMode = adb ? "emulator" : "pc" };
                    var old = await ConfigValidationScriptRunner.ExecuteAsync(new ConfigValidatorDescriptor(script.PluginType, root, item["path"]!.GetValue<string>(), System.Text.Encoding.UTF8.GetString(oldBytes)), script, null, main,
                        extraSnapshots: extraId is null ? [] : [new ConfigValidationExtraSnapshot("software.json", extra)], allowMainWrites: false, allowExtraWrites: false);
                    if (!old.Ran || old.Error.Length != 0 || old.ChangedFiles.Count != 0) throw new InvalidDataException("Legacy validator execution failed");
                    var context = TaskExecutionContext.Unknown("u", "fixture", "preview") with
                    { Mode = script.GameMode, GameTarget = new(bound.Length == 0 ? "none" : adb ? "adb_endpoint" : "executable", bound, null), LogSource = new(variant == "log-stdout" ? "stdout" : "file", true) };
                    var plan = await TaskDiscoveryService.DiscoverAsync(protocol, view, script.PluginType, manifest["version"]!.GetValue<string>(), "u", "fixture", "zh-CN", true, default, context, root, "");
                    string checkId = variant.StartsWith("log-", StringComparison.Ordinal) ? "baah.file_logging" : rule;
                    var check = plan.ConfigAssessment!.Checks.Single(c => c.RuleId == checkId);
                    string expected = variant switch { "empty" or "mismatch" or "adb-mismatch" or "log-off" or "log-absent" => "violated", "absent" or "unbound" or "adb-serial" => "unknown", _ => "satisfied" };
                    int expectedNotifications = variant is "empty" or "mismatch" or "absent" or "adb-mismatch" or "log-off" or "log-absent" or "log-stdout" or "yaml-escaped" ? 1 : 0;
                    if (old.Notifications.Count != expectedNotifications || check.Evaluation != expected)
                        throw new InvalidDataException($"{artifact}/{variant}: notifications {old.Notifications.Count}/{expectedNotifications}; assessment {check.Evaluation}/{expected}");
                    if (before.Any(pair => !File.ReadAllBytes(pair.Key).AsSpan().SequenceEqual(pair.Value))) throw new InvalidDataException("Read-only comparison mutated a file");
                    results.Add(new JsonObject { ["artifact"] = artifact, ["case"] = variant, ["legacyNotifications"] = old.Notifications.Count, ["rule"] = checkId, ["evaluation"] = check.Evaluation, ["effect"] = check.ExecutionEffect, ["status"] = "PASS" });
                    Console.WriteLine("PASS validator " + artifact + "/" + variant);
                    passed = true;
                }
                finally { if (passed) Directory.Delete(root, true); }
            }
        }
        string output = inputs["output"]!.GetValue<string>();
        if (File.Exists(output)) throw new IOException("Report already exists");
        File.WriteAllText(output, new JsonObject { ["legacyCommit"] = inputs["legacyCommit"]!.DeepClone(), ["scope"] = "Original legacy JS and current production discovery through Host Jint, inert files and synthetic config", ["results"] = results }.ToJsonString());
    }

    private static string Encode(JsonObject value, string format) => format == "json" ? value.ToJsonString()
        : string.Join("\n", value.Select(p => p.Key + ": " + (p.Value?.ToJsonString() ?? "null"))) + "\n";
}
