using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Execution.Judgement;

internal sealed partial class TaskRunReducer
{
    private readonly List<HostFailureEvidence> _hostFailures = [];
    internal HostFailureEvidence[] HostFailures => TaskProtocolJson.Copy(_hostFailures.ToArray());
    internal string[] RetryTargets => _effective.Keys.Where(id => !ProtectedByPartial(id)
        && ReducedDailyStatus(id) is "failed" or "blocked").ToArray();
    internal bool CanResume(IEnumerable<string> scope) => scope.All(DailyRetryAllowed);

    internal static void InterruptDailyReport(System.Text.Json.Nodes.JsonObject report)
    {
        var plan = TaskProtocolJson.Read<TaskPlan>(report["originalPlan"]!.ToJsonString());
        var reducer = TaskRunReducer.CreateDaily(report["runId"]!.GetValue<string>(), plan);
        foreach (var result in TaskProtocolJson.Read<TaskEffectiveResult[]>(report["finalTaskResults"]!.ToJsonString()))
        {
            NexusPipeline.Modules.Plugins.TaskProtocolValidation.Require(reducer._effective.ContainsKey(result.TaskId), "checkpoint task identity");
            reducer._effective[result.TaskId] = result with { Status = result.OwnStatus ?? result.Status, OwnStatus = null };
        }
        var attempt = report["attemptReports"]!.AsArray().LastOrDefault();
        reducer._attemptId = attempt?["attemptId"]?.GetValue<string>() ?? "interrupted";
        if (report["incidents"] is { } incidentNodes)
            foreach (var item in TaskProtocolJson.Read<TaskIncidentEvent[]>(incidentNodes.ToJsonString()))
                if (item.AttemptId == reducer._attemptId) reducer._incidents[item.Incident.Id] = item.Incident;
        reducer._selected = reducer._effective.Keys.ToHashSet(StringComparer.Ordinal);
        reducer.RecordHostFailure("process_exit");
        report["finalTaskResults"] = System.Text.Json.Nodes.JsonNode.Parse(TaskProtocolJson.Write(reducer.Results));
        report["summary"] = System.Text.Json.Nodes.JsonNode.Parse(TaskProtocolJson.Write(reducer.Summarize("interrupted")));
        var evidence = report["hostEvidence"] as System.Text.Json.Nodes.JsonArray ?? new();
        if (report["hostEvidence"] is null) report["hostEvidence"] = evidence;
        foreach (var fact in reducer.HostFailures) evidence.Add(System.Text.Json.Nodes.JsonNode.Parse(TaskProtocolJson.Write(fact)));
        foreach (var item in report["attemptReports"]!.AsArray())
            if (item?["lifecycleOutcome"]?.GetValue<string>() == "running")
            {
                item["lifecycleOutcome"] = "interrupted";
                item["taskResults"] = System.Text.Json.Nodes.JsonNode.Parse(TaskProtocolJson.Write(reducer.Results.Where(r => r.LastAttemptId == reducer._attemptId)));
            }
    }

    private void AcceptDaily(TaskObservation observation, TaskEffectiveResult old)
    {
        if (old.LastAttemptId != _attemptId)
        {
            // Native skip records cannot erase an earlier failed execution or its evidence.
            if (observation.Status == "skipped" && old.Status is "succeeded" or "failed" or "skipped") return;
            old = new(observation.TaskId, "pending", "tasks.awaiting_evidence", _attemptId, 0, []);
        }
        if (observation.ExecutionOrdinal < old.ExecutionOrdinal) return;
        if (old.HostEvidenceRefs is { Length: > 0 } && observation.Status != "running") return;
        if (observation.ExecutionOrdinal == old.ExecutionOrdinal && old.Status is not ("pending" or "running"))
        {
            if (observation.Status == "running" || observation.Status == old.Status) return;
            _effective[old.TaskId] = old with { Status = "failed", ReasonCode = "tasks.conflicting_terminal", ReasonText = null };
            return;
        }
        if (observation.ExecutionOrdinal > old.ExecutionOrdinal && observation.Status != "running"
            && (old.ExecutionOrdinal > 0 || observation.FactKind == "flow_ended"))
        {
            _effective[old.TaskId] = old with { Status = "failed", ReasonCode = "tasks.missing_restart", ReasonText = null };
            return;
        }
        _effective[observation.TaskId] = new(observation.TaskId, observation.Status,
            observation.FactKind == "upstream_cancelled" ? "tasks.upstream_cancelled" : observation.ReasonCode,
            _attemptId, observation.ExecutionOrdinal, observation.Evidence.ToArray())
        { ReasonText = observation.ReasonText?.DeepClone().AsObject() };
    }

    internal void RecordHostFailure(string kind)
    {
        if (!Daily) return;
        NexusPipeline.Modules.Plugins.TaskProtocolValidation.Require(kind is "missing_terminal" or "missing_logs"
            or "log_gap" or "format_mismatch" or "observer_error" or "process_exit" or "timeout", "host failure kind");
        string[] affected = _selected.Where(id => _effective[id].Status is "pending" or "running").ToArray();
        if (affected.Length == 0) return;
        string eventId = Guid.NewGuid().ToString("N");
        _hostFailures.Add(new(kind, _attemptId, eventId, affected));
        foreach (string id in affected)
            _effective[id] = _effective[id] with
            { Status = "failed", ReasonCode = "tasks." + kind, ReasonText = null, HostEvidenceRefs = [eventId] };
        Revision++;
    }

    private void FinishDaily(string lifecycle)
    {
        foreach (string id in _selected)
            if (_effective[id].Status == "succeeded" && _incidents.Values.Any(i => i.TaskId == id
                && i.ExecutionOrdinal == _effective[id].ExecutionOrdinal && i.Resolution != "recovered"
                && (i.Kind == "business_error" || _tasks[id].CompletionPolicy == "flow" && i.Kind == "transient_error")))
                _effective[id] = _effective[id] with { Status = "failed", ReasonCode = "tasks.unrecovered_error", ReasonText = null };
        foreach (string id in _selected)
        {
            var state = _effective[id];
            if (state.Status is not ("pending" or "running")) continue;
            if (lifecycle == "cancelled")
            {
                _effective[id] = state with { Status = "cancelled", ReasonCode = "tasks.host_cancelled", ReasonText = null };
                continue;
            }
            string kind = state.Status == "running" ? "missing_terminal" : "missing_logs";
            string eventId = Guid.NewGuid().ToString("N");
            _hostFailures.Add(new(kind, _attemptId, eventId, [id]));
            _effective[id] = state with
            { Status = "failed", ReasonCode = "tasks." + kind, ReasonText = null, HostEvidenceRefs = [eventId] };
        }
        Revision++;
    }

    private string ReducedDailyStatus(string id)
    {
        if (!_effective.TryGetValue(id, out var result)) return "pending";
        string own = result.Status;
        var task = _tasks[id];
        if (own == "succeeded" && result.LastAttemptId == _attemptId
            && _incidents.Values.Any(i => i.TaskId == id && i.ExecutionOrdinal == result.ExecutionOrdinal
                && i.Resolution != "recovered"
                && (i.Kind == "business_error" || task.CompletionPolicy == "flow" && i.Kind == "transient_error")))
            own = "failed";
        if (own is "failed" or "cancelled") return own;
        string[] children = _tasks.Values.Where(t => t.Enabled && t.ParentId == id && t.RequiredForParent)
            .Select(t => ReducedDailyStatus(t.Id)).ToArray();
        if (children.Any(s => s is "failed" or "partial"))
            return own == "succeeded" && task.CompletionPolicy == "authoritative" && !HasIncompleteEvidence(id)
                ? "partial" : "failed";
        return own;
    }

    private bool HasIncompleteEvidence(string id)
    {
        var result = _effective[id];
        if (result.Status == "failed" && (result.HostEvidenceRefs is { Length: > 0 }
            || result.ReasonCode is "tasks.conflicting_terminal" or "tasks.missing_restart")) return true;
        return _tasks.Values.Where(t => t.Enabled && t.ParentId == id && t.RequiredForParent)
            .Any(t => HasIncompleteEvidence(t.Id));
    }

    private bool ProtectedByPartial(string id)
    {
        for (string? current = id; current is not null; current = _tasks[current].ParentId)
            if (ReducedDailyStatus(current) == "partial") return true;
        return false;
    }

    private bool DailyRetryAllowed(string id)
        => !ProtectedByPartial(id) && _tasks[id].WorkflowRole is "daily" or "technical";

    private static TaskSummary SummarizeDaily(Dictionary<string, int> counts)
    {
        (string tone, string outcome) = counts["failed"] > 0 ? ("bad", "failed")
            : counts["partial"] > 0 ? ("warn", "partial")
            : counts["cancelled"] > 0 ? ("muted", "cancelled")
            : counts["blocked"] > 0 ? ("muted", "blocked")
            : counts["total"] == 0 ? ("muted", "no_tasks")
            : counts["pending"] + counts["running"] > 0 ? ("muted", "running")
            : ("ok", "completed");
        return new(tone, outcome, counts, false);
    }
}
