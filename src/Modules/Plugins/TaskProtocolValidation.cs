using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Plugins;

internal static class TaskProtocolValidation
{
    internal static void Discovery(TaskDiscovery discovery)
    {
        Require(discovery.ProtocolVersion is "1.0" or "1.1" && discovery.Type == "discovery", "discovery envelope");
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
        IReadOnlySet<string> selected, IReadOnlySet<(string, int, long)> evidence)
    {
        Require(batch.ProtocolVersion is "1.0" or "1.1" && batch.Type == "observation" && batch.RunId == runId && batch.AttemptId == attemptId, "observation identity");
        Require(batch.Observations is { Length: <= 2048 }, "observation count");
        Require(batch.RunBoundary is "open" or "ended" or "aborted" or "unknown", "boundary");
        Evidence(batch.BoundaryEvidence, evidence, batch.RunBoundary is "ended" or "aborted");
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
            Evidence(observation.Evidence, evidence, observation.Status is not "unknown");
        }
        if (batch.Incidents is { } incidents)
        {
            Require(batch.ProtocolVersion == "1.1" && incidents.Length <= 2048, "incident version/count");
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
