using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Plugins.DataSpecialized;
using NexusPipeline.Modules.Scripts;

internal static class FiniteEditor
{
    internal static async Task<string[]> RunAsync(string root, string artifact)
    {
        if (artifact is not ("BetterGI" or "ZenlessZoneZeroOneDragon" or "MaaStellaSora")) return [];
        string temporary = Path.Combine(Path.GetTempPath(), "nxp-finite-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        bool passed = false;
        try
        {
            string file = artifact == "MaaStellaSora" ? "appsettings.json" : artifact == "BetterGI" ? "config.json" : "one_dragon.yml";
            string original = artifact == "MaaStellaSora" ? "{\"NoAutoStart\":\"False\",\"GlobalStartEnabled\":\"True\",\"Other\":\"42\"}\n" : artifact == "BetterGI" ? "{\"before\":true}\n"
                : "keep: unchanged\ninstance_list:\n- idx: 1\n  name: '01'\n  active: false\n  active_in_od: false\n- idx: 2\n  name: '02'\n  active: true\n  active_in_od: true\n";
            string input = artifact == "BetterGI" ? "NexusPipeline" : "01";
            string location = Path.Combine(temporary, file);
            File.WriteAllText(location, original);
            string plugin = Path.Combine(root, "plugins", "specialized", artifact);
            string editor = Path.Combine(plugin, "data", "editor.js");
            var descriptor = new ConfigEditorDescriptor(artifact, plugin, editor, File.ReadAllText(editor));
            var extras = new[] { new ConfigValidationExtraSnapshot(location, temporary) { SingleFilePath = location, AllowWrite = true } };
            async Task ExecuteAsync()
            {
                var result = await ConfigEditScriptRunner.ExecuteAsync(descriptor, new ScriptInstance(), null, temporary,
                    extraSnapshots: extras, allowMainWrites: false, allowExtraWrites: true,
                    inputFields: new Dictionary<string, string> { ["configInputValue"] = input });
                if (!result.Ran || result.Error.Length != 0) throw new InvalidDataException("Editor failed: " + result.Error);
            }
            await ExecuteAsync();
            string changed = File.ReadAllText(location);
            if (artifact == "BetterGI")
            {
                var json = JsonNode.Parse(changed)!;
                if (json["before"]?.GetValue<bool>() != true || json["selectedOneDragonFlowConfigName"]?.GetValue<string>() != input)
                    throw new InvalidDataException("Editor must preserve unrelated values and select requested flow");
            }
            else if (artifact == "MaaStellaSora")
            {
                var json = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(changed)!;
                if (json["NoAutoStart"] != bool.TrueString || json["GlobalStartEnabled"] != bool.TrueString
                    || json["Other"] != "42")
                    throw new InvalidDataException("Editor must suppress startup while preserving unrelated settings");
            }
            else if (!changed.Contains("keep: unchanged") || changed.Contains("name: '02'")
                || !changed.Contains("active: true") || !changed.Contains("instance_run: 仅运行当前"))
                throw new InvalidDataException("Editor must select one owned profile and preserve unrelated values");
            await ExecuteAsync();
            if (File.ReadAllText(location) != changed) throw new InvalidDataException("Editor repeated preparation must be stable");
            passed = true;
            return [artifact + ".editor-select-and-preserve", artifact + ".editor-repeat"];
        }
        finally { if (passed) Directory.Delete(temporary, true); else Console.Error.WriteLine("Editor evidence: " + temporary); }
    }
}
