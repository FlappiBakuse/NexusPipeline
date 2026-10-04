using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Plugins;

internal static partial class TaskProtocolValidation
{
    private static readonly Dictionary<string, string> DailyFacts = new(StringComparer.Ordinal)
    {
        ["scope_started"] = "running", ["flow_ended"] = "succeeded",
        ["business_succeeded"] = "succeeded", ["business_failed"] = "failed",
        ["upstream_cancelled"] = "failed", ["normal_skip"] = "skipped",
        ["not_executed"] = "blocked",
    };

    private static void DailyTask(TaskDefinition task, string version)
    {
        if (version != "0.2.0")
        {
            Require(task.CompletionPolicy is null && task.WorkflowRole is null
                && task.ObservationContract is null && task.RetryPolicy is null, "daily policy requires 0.2.0");
            return;
        }
        Require(task.CompletionPolicy is "flow" or "authoritative", "completion policy");
        Require(task.WorkflowRole is "daily" or "technical" or "manual_only", "workflow role");
        Require(task.WorkflowRole != "manual_only" || !task.Enabled && !task.CountsAsUnit, "manual task scope");
        Require(task.WorkflowRole != "daily" || task.Role == "business", "daily business role");
        Require(!task.Enabled || task.Detection == "supported", "daily task needs observable boundaries");
        var contract = task.ObservationContract;
        Require(contract is not null, "observation contract required");
        Text(contract!.RuleSetId);
        Require(contract.Sources is { Length: > 0 and <= 8 }
            && contract.Sources.Distinct(StringComparer.Ordinal).Count() == contract.Sources.Length, "observation sources");
        foreach (string source in contract.Sources) Text(source);
        Require(contract.Rules is { Length: > 0 and <= 64 }, "observation rules");
        var rules = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in contract.Rules)
        {
            Require(rule is not null, "null observation rule");
            Text(rule.Id);
            Require(rules.Add(rule.Id) && DailyFacts.ContainsKey(rule.Kind), "observation rule identity/kind");
            Require(rule.Kind != "business_succeeded" || task.CompletionPolicy == "authoritative", "success authority");
            Require(rule.Kind != "flow_ended" || task.CompletionPolicy == "flow", "flow authority");
        }
        var retry = task.RetryPolicy;
        Require(retry is not null && retry.Mode is "selective_config" or "native_resume", "retry policy");
        Require(retry!.LimitRefs is { Length: <= 32 }
            && retry.LimitRefs.Distinct(StringComparer.Ordinal).Count() == retry.LimitRefs.Length, "retry limit references");
        foreach (string reference in retry.LimitRefs) Text(reference);
    }

    private static void DailyObservation(TaskObservation observation, string version)
    {
        if (version != "0.2.0")
        {
            Require(observation.FactKind is null, "daily fact requires 0.2.0");
            return;
        }
        Require(observation.FactKind is not null
            && DailyFacts.TryGetValue(observation.FactKind, out string? status)
            && status == observation.Status, "daily fact/status");
    }

    internal static void DailyAuthority(TaskDefinition task, TaskObservation observation)
    {
        var contract = task.ObservationContract!;
        Require(observation.Evidence.Length > 0 && observation.Evidence.All(e =>
            contract.Sources.Contains(e.SourceId, StringComparer.Ordinal)
            && contract.Rules.Any(rule => rule.Id == e.RuleId))
            && observation.Evidence.Any(e => contract.Rules.Any(rule => rule.Id == e.RuleId && rule.Kind == observation.FactKind)),
            "observation outside frozen rule authority");
    }
}
