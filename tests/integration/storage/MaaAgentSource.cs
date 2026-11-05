using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusPipeline.StorageTests;

internal static class MaaAgentSource
{
    internal static void Prepare(ProjectSourceFixture project, string archive, string marker)
    {
        MaaInitializationSource.PrepareProgram(project, archive, agent: true);
        var path = Path.Combine(project.Path, "assets/interface.json");
        var pi = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        pi["agent"] = new JsonObject { ["child_exec"] = "../init/run.exe",
            ["child_args"] = new JsonArray("maa-agent-fixture", marker) };
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(pi));
        var pipeline = Path.Combine(project.Path, "assets/resource/pipeline/main.json");
        var nodes = JsonNode.Parse(File.ReadAllBytes(pipeline))!.AsObject();
        var task = nodes["DailyEntry"]!.AsObject();
        task["action"] = "Custom"; task["custom_action"] = "OwnedAgentAction";
        task["custom_action_param"] = new JsonObject { ["text"] = "中文 Agent" }; task.Remove("target");
        File.WriteAllBytes(pipeline, JsonSerializer.SerializeToUtf8Bytes(nodes));
    }
}
