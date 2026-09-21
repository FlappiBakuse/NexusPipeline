using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Execution.Judgement;

internal static class TaskDiscoveryService
{
    internal static async Task<TaskPlan> DiscoverAsync(TaskProtocolDescriptor protocol, TaskConfigView view,
        string pluginId, string pluginVersion, string userId, string scriptId, string locale, bool preview, CancellationToken token)
    {
        var result = await TaskProtocolScriptRunner.ExecuteAsync<TaskDiscovery>(protocol.DiscoverScript,
            new { protocolVersion = "1.0", phase = "discover", origin = preview ? "preview" : "run",
                pluginId, userId, scriptInstanceId = scriptId, configResources = view.ConfigResources, locale },
            view.ReadConfig, view.ReadResource, preview, token).ConfigureAwait(false);
        TaskProtocolValidation.Discovery(result);
        var behavior = result.BehaviorFields.Select(field =>
        {
            var resource = view.Snapshot(field.ResourceId);
            return new { field.ResourceId, field.Selector,
                value = Canonical(new TaskConfigDocument(resource.Bytes, resource.Format).ReadSelection(field.Selector)) };
        }).OrderBy(field => field.ResourceId, StringComparer.Ordinal).ThenBy(field => field.Selector.ToJsonString(), StringComparer.Ordinal).ToArray();
        view.VerifyUnchanged();
        string signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TaskProtocolJson.Write(new
        { pluginId, pluginVersion, result.Coverage, result.Tasks, result.SelectionFields, behavior })))).ToLowerInvariant();
        return new("1.0", Guid.NewGuid().ToString("N"), preview ? "preview" : "run", pluginId,
            pluginVersion, DateTimeOffset.UtcNow, signature, result.Coverage,
            TaskProtocolJson.Copy(result.Tasks), TaskProtocolJson.Copy(result.Diagnostics))
        { SelectionFields = TaskProtocolJson.Copy(result.SelectionFields),
            BehaviorSignature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TaskProtocolJson.Write(behavior)))).ToLowerInvariant() };
    }

    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Canonical(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
        _ => node?.DeepClone(),
    };
}
