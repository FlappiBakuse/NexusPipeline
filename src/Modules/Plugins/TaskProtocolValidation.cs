using System.Text.Json.Nodes;
using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Plugins;

internal static class TaskProtocolValidation
{
    internal static void Discovery(TaskDiscovery discovery)
    {
        Require(discovery.ProtocolVersion == "0.1.0" && discovery.Type == "discovery", "discovery envelope");
        Require(discovery.Coverage is "complete" or "partial" or "unsupported", "coverage");
        Require(discovery.Tasks is { Length: <= 1024 }, "task count");
        Diagnostics(discovery.Diagnostics, discovery.ProtocolVersion);
        Require(discovery.SelectionFields is { Length: <= 2048 }, "selection field count");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        Require(discovery.BehaviorFields is { Length: <= 2048 }, "behavior field count");
        foreach (var field in discovery.BehaviorFields)
        {
            Require(field is not null, "null behavior field"); Text(field.ResourceId);
            Require(field.Selector is { Count: > 0 and <= 32 }, "behavior selector");
        }
        foreach (var field in discovery.SelectionFields)
        {
            Require(field is not null, "null selection field");
            Text(field.ResourceId);
            Require(field.Purpose is "selection" or "cursor" && field.Selector is { Count: > 0 and <= 32 }, "selection field");
            foreach (var token in field.Selector)
            {
                if (token is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var property)) { Text(property); continue; }
                Require(token is System.Text.Json.Nodes.JsonObject, "selector token");
                var selector = (System.Text.Json.Nodes.JsonObject)token;
                if (selector.ContainsKey("by"))
                {
                    Require(selector.Count == 2 && selector.ContainsKey("value"), "identity selector fields");
                    Text(selector["by"]?.GetValue<string>());
                    Require(selector["value"] is System.Text.Json.Nodes.JsonValue, "identity selector value");
                }
                else
                {
                    Require(selector.Count == 3 && selector.ContainsKey("guardValue"), "guard selector fields");
                    Require(selector["index"] is System.Text.Json.Nodes.JsonValue index && index.TryGetValue<int>(out var number) && number >= 0, "guard index");
                    Text(selector["guardKey"]?.GetValue<string>());
                    Require(selector["guardValue"] is not null, "guard value");
                }
            }
            Require(fields.Add(field.ResourceId + "\n" + field.Selector.ToJsonString()), "duplicate selection field");
        }
        var tasks = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal);
        foreach (var task in discovery.Tasks)
        {
            Require(task is not null, "null task");
            Text(task.Id); Text(task.Name); Text(task.SourceKey); Text(task.RetryUnitId);
            TaskDisplaySnapshot.ValidateReference(task.NameText, discovery.ProtocolVersion);
            Require(tasks.TryAdd(task.Id, task), "duplicate task identity");
            Require(task.Role is "business" or "technical" or "cleanup", "role");
            Require(task.RetryRisk is "safe" or "conditional" or "unsafe" or "unknown", "risk");
            Require(task.Detection is "supported" or "limited" or "unsupported", "detection");
            Require(task.Order >= 0 && task.Dependencies is { Length: <= 128 }, "order/dependencies");
            Require(task.Dependencies.Distinct(StringComparer.Ordinal).Count() == task.Dependencies.Length, "duplicate dependency");
            if (task.ParentId is not null) Text(task.ParentId);
            if (task.ConfigRef is not null) Text(task.ConfigRef);
            Require(!task.CountsAsUnit || task.ParentId is null, "child cannot count twice");
        }
        foreach (var task in tasks.Values)
        {
            Require(tasks.ContainsKey(task.RetryUnitId), "missing retry unit");
            Require(task.ParentId is null || tasks.ContainsKey(task.ParentId), "missing parent");
            foreach (string id in task.Dependencies) { Text(id); Require(tasks.ContainsKey(id), "missing dependency"); }
        }
        CheckGraph(tasks, false);
        CheckGraph(tasks, true);
    }

    /// <summary>
    /// Validates the configuration assessment against the frozen manifest and
    /// the resources that Host actually exposed. A well-shaped but unauthorized
    /// check is still rejected; it cannot become an implicit pass.
    /// </summary>
    internal static void ConfigAssessment(
        TaskDiscovery discovery,
        TaskProtocolDescriptor protocol,
        IReadOnlySet<string> declaredConfigIds,
        IReadOnlySet<string> declaredResourceIds)
    {
        Require(protocol.Version == "0.1.0", "unsupported task protocol");
        TaskConfigAssessment? assessment = discovery.ConfigAssessment;
        Require(assessment is not null, "config assessment required");
        if (assessment is null) throw new InvalidDataException("protocol_error: config assessment required");
        Require(assessment.SchemaVersion == "1" && assessment.Checks is { Length: > 0 and <= 128 }, "config assessment envelope");

        var declarations = protocol.ConfigRules.ToDictionary(rule => rule.RuleId, StringComparer.Ordinal);
        Require(declarations.Count == protocol.ConfigRules.Length && declarations.Count > 0, "config rule declarations");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var taskIds = discovery.Tasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        // Optional manifest resources may be absent from the frozen view. Their
        // locations are still valid declarations; the script must report
        // unknown rather than making the assessment envelope invalid.
        var configIds = declaredConfigIds
            .Where(id => id.StartsWith("config:", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var resourceIds = protocol.ReadResources
            .Select(resource => resource.Id)
            .Concat(declaredResourceIds.Where(id => !id.StartsWith("config:", StringComparison.Ordinal)))
            .ToHashSet(StringComparer.Ordinal);
        var environmentIds = protocol.EnvironmentChecks.Select(check => check.Id).ToHashSet(StringComparer.Ordinal);
        foreach (TaskConfigCheck check in assessment.Checks)
        {
            Require(check is not null, "null config check");
            Text(check.RuleId);
            Require(declarations.TryGetValue(check.RuleId, out TaskConfigRuleDescriptor? declaration), "undeclared config rule");
            Require(check.Evaluation is "satisfied" or "violated" or "unknown" or "not_applicable", "config evaluation");
            Require(check.Severity is "info" or "warning" or "error", "config severity");
            Require(check.ExecutionEffect is "none" or "warn" or "block", "config execution effect");
            Require(check.Scope is { Count: > 0 and <= 2 }, "config scope");
            string scopeKey = ValidateScope(check.Scope, taskIds);
            Require(seen.Add(check.RuleId + "\n" + scopeKey), "duplicate config check");
            Require(check.Locations is { Count: <= 8 }, "config locations");
            foreach (JsonNode? location in check.Locations)
                ValidateLocation(location, configIds, resourceIds, environmentIds);
            Require(check.Actions is { Count: <= 3 }, "config actions");
            foreach (JsonNode? action in check.Actions)
            {
                Require(action is System.Text.Json.Nodes.JsonObject { Count: 1 } actionObject
                    && actionObject["kind"]?.GetValue<string>() is "open_binding_editor" or "open_script_settings" or "refresh_plan",
                    "config action");
            }
            if (check.Evaluation is "violated" or "unknown")
            {
                Require(check.ReasonText is not null, "config reason text");
                TaskDisplaySnapshot.ValidateReference(check.ReasonText, protocol.Version);
            }
            else
            {
                Require(check.ExecutionEffect == "none", "satisfied config effect");
                if (check.ReasonText is not null) TaskDisplaySnapshot.ValidateReference(check.ReasonText, protocol.Version);
            }
            if (check.ExecutionEffect == "block")
                Require(declaration!.Criticality == "critical_when_applicable", "unapproved config block");
        }
        foreach (TaskConfigRuleDescriptor declaration in protocol.ConfigRules.Where(rule => rule.Required))
            Require(seen.Any(key => key.StartsWith(declaration.RuleId + "\n", StringComparison.Ordinal)), "required config rule missing");
    }

    private static string ValidateScope(System.Text.Json.Nodes.JsonObject scope, IReadOnlySet<string> taskIds)
    {
        string kind = scope["kind"]?.GetValue<string>() ?? "";
        if (kind is "binding" or "queue")
        {
            Require(scope.Count == 1, "config scope fields");
            return kind;
        }
        string? taskId = scope["taskId"]?.GetValue<string>();
        Require(kind == "task" && scope.Count == 2 && taskId is { Length: > 0 }
            && taskIds.Contains(taskId), "config task scope");
        return kind + ":" + taskId;
    }

    private static void ValidateLocation(
        JsonNode? location,
        IReadOnlySet<string> configIds,
        IReadOnlySet<string> resourceIds,
        IReadOnlySet<string> environmentIds)
    {
        System.Text.Json.Nodes.JsonObject? objectValue = location as System.Text.Json.Nodes.JsonObject;
        Require(objectValue is not null, "config location");
        if (objectValue is null) throw new InvalidDataException("protocol_error: config location");
        string source = objectValue["source"]?.GetValue<string>() ?? "";
        if (source is "config" or "resource")
        {
            string? resourceId = objectValue["resourceId"]?.GetValue<string>();
            System.Text.Json.Nodes.JsonArray? selector = objectValue["selector"] as System.Text.Json.Nodes.JsonArray;
            Require(objectValue.Count == 3 && resourceId is { Length: > 0 }
                && (source == "config"
                    ? configIds.Contains(resourceId)
                    : resourceIds.Contains(resourceId))
                && selector is not null,
                "config resource location");
            ValidateSelector(selector ?? throw new InvalidDataException("protocol_error: config selector"));
            return;
        }
        if (source == "context")
        {
            string field = objectValue["field"]?.GetValue<string>() ?? "";
            Require(objectValue.Count == 2 && System.Text.RegularExpressions.Regex.IsMatch(field, "^[A-Za-z0-9_.:-]{1,160}$"), "config context location");
            return;
        }
        Require(source == "environment" && objectValue.Count == 2
            && objectValue["inspectionId"]?.GetValue<string>() is { Length: > 0 } inspectionId
            && environmentIds.Contains(inspectionId), "config environment location");
    }

    private static void ValidateSelector(System.Text.Json.Nodes.JsonArray selector)
    {
        Require(selector.Count is > 0 and <= 32, "config location selector");
        foreach (JsonNode? token in selector)
        {
            if (token is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out string? property))
            {
                Text(property);
                Require(property is not ("__proto__" or "prototype" or "constructor"), "reserved config selector");
                continue;
            }
            System.Text.Json.Nodes.JsonObject? selectorObject = token as System.Text.Json.Nodes.JsonObject;
            Require(selectorObject is not null, "config selector token");
            if (selectorObject is null) throw new InvalidDataException("protocol_error: config selector token");
            if (selectorObject.ContainsKey("by"))
            {
                Require(selectorObject.Count == 2 && selectorObject["by"]?.GetValue<string>() is { Length: > 0 }
                    && selectorObject.ContainsKey("value"), "config identity selector");
            }
            else
            {
                Require(selectorObject.Count == 3 && selectorObject["index"]?.GetValue<int>() >= 0
                    && selectorObject["guardKey"]?.GetValue<string>() is { Length: > 0 }
                    && selectorObject.ContainsKey("guardValue"), "config guard selector");
            }
        }
    }

    private static void CheckGraph(Dictionary<string, TaskDefinition> tasks, bool parents)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id, int depth)
        {
            Require(depth <= (parents ? 8 : 128), "graph depth limit");
            if (!parents && done.Contains(id)) return;
            Require(visiting.Add(id), "task graph cycle");
            var task = tasks[id];
            foreach (string next in parents ? task.ParentId is {} p ? new[] { p } : [] : task.Dependencies) Visit(next, depth + 1);
            visiting.Remove(id);
            done.Add(id);
        }
        foreach (string id in tasks.Keys) Visit(id, 1);
    }

    internal static void Observation(TaskObservationBatch batch, string runId, string attemptId,
        IReadOnlySet<string> selected, IReadOnlySet<(string, int, long)> evidence,
        IReadOnlySet<string>? structuredEvidence = null)
    {
        Require(batch.ProtocolVersion == "0.1.0" && batch.Type == "observation" && batch.RunId == runId && batch.AttemptId == attemptId, "observation identity");
        Require(batch.Observations is { Length: <= 2048 }, "observation count");
        Require(batch.RunBoundary is "open" or "ended" or "aborted" or "unknown", "boundary");
        Require(structuredEvidence is null ? batch.StructuredEvidenceVersion is null : batch.StructuredEvidenceVersion == 1,
            "structured evidence must be authenticated by Host");
        bool structuredBoundary = StructuredEvidence(batch.BoundaryStructuredEvidenceRefs, structuredEvidence);
        Evidence(batch.BoundaryEvidence, evidence, batch.RunBoundary is "ended" or "aborted" && !structuredBoundary);
        Diagnostics(batch.Diagnostics, batch.ProtocolVersion);
        Require(batch.CursorState is null || System.Text.Encoding.UTF8.GetByteCount(batch.CursorState.ToJsonString()) <= 64 * 1024, "adapter cursor limit");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in batch.Observations)
        {
            Require(observation is not null, "null observation");
            Text(observation.Id); Text(observation.TaskId); Text(observation.ReasonCode);
            TaskDisplaySnapshot.ValidateReference(observation.ReasonText, batch.ProtocolVersion);
            Require(ids.Add(observation.Id) && selected.Contains(observation.TaskId), "observation task/id");
            Require(observation.ExecutionOrdinal > 0, "execution ordinal");
            Require(observation.Status is "running" or "succeeded" or "failed" or "skipped" or "blocked" or "unknown", "observation status");
            Require(observation.Status != "skipped" || observation.SkipKind is "satisfied" or "inapplicable", "skip kind");
            bool structured = StructuredEvidence(observation.StructuredEvidenceRefs, structuredEvidence);
            Evidence(observation.Evidence, evidence, observation.Status is not "unknown" && !structured);
        }
        if (batch.Incidents is { } incidents)
        {
            Require(incidents.Length <= 2048, "incident count");
            var events = new HashSet<string>(StringComparer.Ordinal);
            foreach (var incident in incidents)
            {
                Require(incident is not null, "null incident");
                Text(incident.Id); Text(incident.ScopeId); Text(incident.ReasonCode);
                Require(incident.TaskId is null || selected.Contains(incident.TaskId), "incident task");
                Require(incident.ExecutionOrdinal > 0 && incident.Kind is "transient_error" or "business_error" or "unattributed_error", "incident kind/ordinal");
                Require(incident.Kind != "unattributed_error" || incident.TaskId is null, "unattributed incident task");
                Require(incident.Resolution is "open" or "recovered" or "terminal", "incident resolution");
                Require(events.Add(incident.Id + "\n" + incident.Resolution), "duplicate incident event");
                TaskDisplaySnapshot.ValidateReference(incident.ReasonText, batch.ProtocolVersion);
                Evidence(incident.Evidence, evidence, true);
            }
        }
    }

    private static bool StructuredEvidence(string[]? references, IReadOnlySet<string>? known)
    {
        if (references is null) return false;
        Require(known is not null && references.Length is > 0 and <= 8
            && references.Distinct(StringComparer.Ordinal).Count() == references.Length,
            "structured evidence references");
        foreach (string id in references) { Text(id); Require(known!.Contains(id), "structured evidence outside current attempt"); }
        return true;
    }

    private static void Evidence(TaskEvidence[] items, IReadOnlySet<(string, int, long)> known, bool required)
    {
        Require(items is { Length: <= 8 } && (!required || items.Length > 0), "evidence count");
        foreach (var item in items)
        {
            Require(item is not null, "null evidence");
            Text(item.SourceId); Text(item.RuleId);
            Require(item.Epoch >= 0 && item.Sequence >= 0 && known.Contains((item.SourceId, item.Epoch, item.Sequence)), "evidence outside current attempt");
        }
    }

    private static void Diagnostics(TaskDiagnostic[] items, string version)
    {
        Require(items is { Length: <= 128 }, "diagnostic count");
        foreach (var item in items)
        {
            Require(item is not null, "null diagnostic");
            Text(item.Code); Text(item.Message);
            TaskDisplaySnapshot.ValidateReference(item.ReasonText, version);
            if (item.TaskId is not null) Text(item.TaskId);
        }
    }

    private static void Text(string? value) => Require(value is { Length: > 0 and <= 512 }, "text limit");
    internal static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string error)
    {
        if (!condition) throw new InvalidDataException("protocol_error: " + error);
    }
}
