using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Plugins;

// Explicit read-only installation roots plus synthetic account settings. Never executes upstream code.
internal static class RuntimeInstallationProbe
{
    internal static async Task RunAsync(string plugins, string matrixPath)
    {
        var matrix = JsonNode.Parse(File.ReadAllText(matrixPath))!.AsObject();
        var results = new JsonArray();
        foreach (var row in matrix["cases"]!.AsArray())
        {
            string artifact = row!["artifact"]!.GetValue<string>();
            if (artifact is not ("OkWutheringWaves" or "OkNTE")) throw new InvalidDataException("Unsupported installation probe");
            string fixtureName = artifact == "OkNTE" ? "r2-oknte-default-empty.json" : "r2-okww-farm-tacet.json";
            var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(plugins, "tools", "task-protocol", "fixtures", fixtureName)))!;
            string package = Path.Combine(plugins, "plugins", "specialized", artifact);
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(package, "plugin.json")))!.AsObject();
            var protocol = TaskProtocolManifest.Freeze(manifest, package)!;
            string temporary = Path.Combine(Path.GetTempPath(), "nxp-installation-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            bool passed = false;
            try
            {
                foreach (var entry in fixture["resources"]!.AsArray())
                {
                    string id = entry!["id"]!.GetValue<string>();
                    if (!id.StartsWith("config:", StringComparison.Ordinal)) continue;
                    string name = id[7..];
                    if (name != Path.GetFileName(name)) throw new InvalidDataException("Fixture config path");
                    File.WriteAllText(Path.Combine(temporary, name), entry["text"]!.GetValue<string>());
                }
                var view = TaskConfigViewFactory.Capture(temporary, row["root"]!.GetValue<string>(), [], protocol.ReadResources);
                var plan = await TaskDiscoveryService.DiscoverAsync(protocol, view, manifest["name"]!.GetValue<string>(),
                    manifest["version"]!.GetValue<string>(), "synthetic-user", "installation-probe", "zh-CN", true, default);
                var distribution = plan.ConfigAssessment!.Checks.Single(c => c.RuleId == "okscript.distribution");
                string expected = row["expectedEvaluation"]!.GetValue<string>();
                if (distribution.Evaluation != expected) throw new InvalidDataException("Unexpected distribution assessment: " + row["id"]);
                results.Add(new JsonObject { ["id"] = row["id"]!.DeepClone(), ["evaluation"] = distribution.Evaluation,
                    ["executionEffect"] = distribution.ExecutionEffect, ["coverage"] = plan.Coverage, ["status"] = "PASS" });
                passed = true;
                Console.WriteLine("PASS installation " + row["id"]);
            }
            finally { if (passed) Directory.Delete(temporary, true); }
        }
        if (results.Count == 0) throw new InvalidDataException("Zero installation cases");
        string output = matrix["output"]!.GetValue<string>();
        if (File.Exists(output)) throw new IOException("Refusing to overwrite existing report");
        File.WriteAllText(output, new JsonObject { ["scope"] = "Read-only official installation bytes with synthetic account config; no upstream code executed", ["results"] = results }.ToJsonString());
    }
}
