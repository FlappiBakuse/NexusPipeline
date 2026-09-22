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
        string pluginId, string pluginVersion, string userId, string scriptId, string locale, bool preview, CancellationToken token,
        TaskExecutionContext? executionContext = null, string scriptRoot = "", string scriptExecutable = "")
    {
        TaskExecutionContext context = executionContext ?? TaskExecutionContext.Unknown(userId, scriptId, preview ? "preview" : "pre_launch");
        TaskEnvironmentProbe? probe = protocol.Version == "1.2"
            ? new TaskEnvironmentProbe(protocol.EnvironmentChecks, view, context, scriptRoot, scriptExecutable)
            : null;
        var result = await TaskProtocolScriptRunner.ExecuteAsync<TaskDiscovery>(protocol.DiscoverScript,
            new { protocolVersion = protocol.Version, phase = "discover", origin = preview ? "preview" : "run",
                pluginId, userId, scriptInstanceId = scriptId, configResources = view.ConfigResources, locale,
                executionContext = context },
            view.ReadConfig, view.ReadResource, preview, token, probe is null ? null : probe.Inspect).ConfigureAwait(false);
        TaskProtocolValidation.Discovery(result);
        TaskProtocolValidation.Require(result.ProtocolVersion == protocol.Version, "negotiated discovery version");
        TaskProtocolValidation.ConfigAssessment(result, protocol, view);
        var behavior = result.BehaviorFields.Select(field =>
        {
            var resource = view.Snapshot(field.ResourceId);
            return new { field.ResourceId, field.Selector,
                value = Canonical(new TaskConfigDocument(resource.Bytes, resource.Format).ReadSelection(field.Selector)) };
        }).OrderBy(field => field.ResourceId, StringComparer.Ordinal).ThenBy(field => field.Selector.ToJsonString(), StringComparer.Ordinal).ToArray();
        view.VerifyUnchanged();
        TaskReadiness? readiness = result.ConfigAssessment is { } assessment
            ? BuildReadiness(assessment, view, context)
            : null;
        string signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TaskProtocolJson.Write(new
        { pluginId, pluginVersion, result.Coverage, Tasks = result.Tasks.Select(t => t with { Name = "", NameText = null }), result.SelectionFields, behavior })))).ToLowerInvariant();
        return new(protocol.Version, Guid.NewGuid().ToString("N"), preview ? "preview" : "run", pluginId,
            pluginVersion, DateTimeOffset.UtcNow, signature, result.Coverage,
            TaskProtocolJson.Copy(result.Tasks), TaskProtocolJson.Copy(result.Diagnostics))
        { SelectionFields = TaskProtocolJson.Copy(result.SelectionFields),
            DisplaySnapshot = protocol.Localization is { } texts ? (texts with { PluginId = pluginId, PluginVersion = pluginVersion })
                .Select(result.Tasks.Select(t => t.NameText)
                    .Concat(result.Diagnostics.Select(d => d.ReasonText))
                    .Concat(result.ConfigAssessment?.Checks.Select(c => c.ReasonText) ?? [])) : null,
            BehaviorSignature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TaskProtocolJson.Write(behavior)))).ToLowerInvariant(),
            ConfigAssessment = result.ConfigAssessment is null ? null : TaskProtocolJson.Copy(result.ConfigAssessment),
            CurrentReadiness = readiness };
    }

    private static TaskReadiness BuildReadiness(TaskConfigAssessment assessment, TaskConfigView view, TaskExecutionContext context)
    {
        bool blocked = assessment.Checks.Any(check => check.ExecutionEffect == "block"
            && check.Evaluation is "violated" or "unknown");
        bool unknown = assessment.Checks.Any(check => check.Evaluation == "unknown");
        bool attention = assessment.Checks.Any(check => check.ExecutionEffect == "warn" || check.Evaluation == "violated");
        string state = blocked ? "blocked" : unknown ? "unknown" : attention ? "attention" : "ready";
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TaskProtocolJson.Write(context)))).ToLowerInvariant();
        return new(state, false, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), view.RevisionToken, fingerprint);
    }

    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Canonical(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
        _ => node?.DeepClone(),
    };
}
