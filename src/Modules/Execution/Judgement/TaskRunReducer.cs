using NexusPipeline.Modules.Plugins;
using NexusPipeline.Modules.Plugins.Contracts;

namespace NexusPipeline.Modules.Execution.Judgement;

internal sealed record TaskEffectiveResult(string TaskId, string Status, string ReasonCode,
    string LastAttemptId, int ExecutionOrdinal, TaskEvidence[] Evidence)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public System.Text.Json.Nodes.JsonObject? ReasonText { get; init; }
}
internal sealed record TaskSummary(string Tone, string Outcome, Dictionary<string, int> Counts, bool Recovered);
internal sealed record TaskRetrySelection(string Decision, string ReasonCode, string[] IncludedTaskIds,
    string[] PrerequisiteTaskIds, string[] ExpandedUnitIds);
internal sealed record TaskIncidentEvent(string AttemptId, TaskIncident Incident);

/// <summary>One run, one owner. Every accepted fact belongs to a host-generated attempt/log identity.</summary>
internal sealed class TaskRunReducer
{
    private readonly TaskPlan _plan;
    private readonly Dictionary<string, TaskDefinition> _tasks;
    private readonly Dictionary<string, TaskEffectiveResult> _effective = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _accepted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskIncident> _incidents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _incidentReplays = new(StringComparer.Ordinal);
    private readonly List<TaskIncidentEvent> _incidentHistory = [];
    private int _incidentBytes;
    private HashSet<string> _selected = new(StringComparer.Ordinal);
    private HashSet<(string, int, long)> _evidence = new();
    private string _attemptId = "";
    private int _attemptNumber;
    private bool _hadFailure;
    internal string RunId { get; }
    internal string RunBoundary { get; private set; } = "unknown";
    internal long Revision { get; private set; }

    internal TaskRunReducer(string runId, TaskPlan plan)
    {
        _plan = TaskProtocolJson.Copy(plan);
        TaskProtocolValidation.Discovery(new TaskDiscovery { ProtocolVersion = plan.ProtocolVersion, Type = "discovery",
            Coverage = plan.Coverage, Tasks = plan.Tasks, Diagnostics = plan.Diagnostics,
            ConfigAssessment = plan.ConfigAssessment });
        _tasks = _plan.Tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        RunId = runId;
        foreach (var task in _tasks.Values.Where(t => t.Enabled)) _effective[task.Id] = new(task.Id, "pending", "tasks.not_started", "", 0, []);
    }

    internal TaskPlan OriginalPlan => TaskProtocolJson.Copy(_plan);
    internal TaskEffectiveResult[] AcceptedResults => TaskProtocolJson.Copy(_effective.Values.ToArray());
    internal TaskIncidentEvent[] IncidentHistory => TaskProtocolJson.Copy(_incidentHistory.ToArray());
    internal TaskEffectiveResult[] Results => TaskProtocolJson.Copy(_effective.Values.Select(r =>
        r with { Status = ReducedStatus(r.TaskId) }).ToArray());

    internal void BeginAttempt(string id, int number, IEnumerable<string> selected)
    {
        var list = selected.ToArray();
        TaskProtocolValidation.Require(!string.IsNullOrWhiteSpace(id) && id != _attemptId && number > _attemptNumber, "attempt order");
        TaskProtocolValidation.Require(list.Distinct(StringComparer.Ordinal).Count() == list.Length
            && list.All(key => _tasks.TryGetValue(key, out var t) && (t.Enabled || t.Role == "technical")), "attempt selection");
        _selected = list.ToHashSet(StringComparer.Ordinal);
        _attemptId = id; _attemptNumber = number; _accepted.Clear(); _incidents.Clear(); _incidentReplays.Clear(); _evidence.Clear(); RunBoundary = "unknown";
        foreach (string key in _selected) _effective[key] = new(key, "pending", "tasks.awaiting_evidence", id, 0, []);
        Revision++;
    }

    internal void Accept(TaskObservationBatch batch, TaskLogBatch logs)
    {
        var available = new HashSet<(string, int, long)>(_evidence);
        foreach (var line in logs.Records) available.Add((line.SourceId, line.Epoch, line.Sequence));
        TaskProtocolValidation.Observation(batch, RunId, _attemptId, _selected, available);
        TaskProtocolValidation.Require(batch.ProtocolVersion == _plan.ProtocolVersion, "negotiated observation version");
        TaskProtocolValidation.Require(available.Count <= 262144 && _accepted.Count + batch.Observations.Count(o => !_accepted.ContainsKey(o.Id)) <= 65536,
            "resource_limit: attempt evidence ledger");
        // Entire batch is validated before mutation. Invalid replays never acknowledge a cursor.
        foreach (var observation in batch.Observations)
            if (_accepted.TryGetValue(observation.Id, out var previous))
                TaskProtocolValidation.Require(previous == TaskProtocolJson.Write(observation), "observation id reused with different facts");
        // Validate incident transitions on a copy before committing any observations or incidents.
        var pending = new Dictionary<string, TaskIncident>(_incidents, StringComparer.Ordinal);
        var additions = new List<(string Key, string Json, TaskIncident Incident)>();
        foreach (var incident in batch.Incidents ?? [])
        {
            string replayKey = incident.Id + "\n" + incident.Resolution;
            string json = TaskProtocolJson.Write(incident);
            if (_incidentReplays.TryGetValue(replayKey, out var previous))
            {
                TaskProtocolValidation.Require(previous == json, "incident event reused with different facts");
                continue;
            }
            if (pending.TryGetValue(incident.Id, out var old))
                TaskProtocolValidation.Require(old.Resolution == "open" && incident.Resolution is "recovered" or "terminal"
                    && old.TaskId == incident.TaskId && old.ScopeId == incident.ScopeId && old.ExecutionOrdinal == incident.ExecutionOrdinal
                    && old.Kind == incident.Kind && old.ReasonCode == incident.ReasonCode
                    && TaskProtocolJson.Write(old.ReasonText) == TaskProtocolJson.Write(incident.ReasonText)
                    && old.Evidence.All(e => incident.Evidence.Contains(e))
                    && incident.Evidence.Any(e => !old.Evidence.Contains(e)), "incident transition");
            else TaskProtocolValidation.Require(incident.Resolution == "open", "incident must begin open");
            pending[incident.Id] = incident;
            additions.Add((replayKey, json, incident));
        }
        TaskProtocolValidation.Require(_incidentHistory.Count + additions.Count <= 4096, "resource_limit: incident history");
        int addedBytes = additions.Sum(e => System.Text.Encoding.UTF8.GetByteCount(TaskProtocolJson.Write(new TaskIncidentEvent(_attemptId, e.Incident))));
        TaskProtocolValidation.Require(_incidentBytes + addedBytes <= 256 * 1024, "resource_limit: incident bytes");
        foreach (var observation in batch.Observations)
        {
            if (!_accepted.TryAdd(observation.Id, TaskProtocolJson.Write(observation))) continue;
            var old = _effective[observation.TaskId];
            if (observation.ExecutionOrdinal < old.ExecutionOrdinal) continue;
            bool terminal = old.Status is not ("pending" or "running");
            if (observation.ExecutionOrdinal == old.ExecutionOrdinal && terminal)
            {
                if (observation.Status == "running" || observation.Status == old.Status) continue;
                _effective[observation.TaskId] = old with { Status = "unknown", ReasonCode = "protocol.conflicting_evidence", ReasonText = null };
                continue;
            }
            if (old.ExecutionOrdinal > 0 && observation.ExecutionOrdinal > old.ExecutionOrdinal && observation.Status != "running")
            {
                _effective[observation.TaskId] = old with { Status = "unknown", ReasonCode = "protocol.missing_restart", ReasonText = null };
                continue;
            }
            string status = observation.Status;
            if (_tasks[observation.TaskId].Detection == "unsupported" && status is "succeeded" or "skipped") status = "unknown";
            _effective[observation.TaskId] = new(observation.TaskId, status, observation.ReasonCode,
                _attemptId, observation.ExecutionOrdinal, observation.Evidence.ToArray()) { ReasonText = observation.ReasonText?.DeepClone().AsObject() };
            _hadFailure |= status == "failed";
        }
        _evidence = available;
        foreach (var entry in additions)
        {
            var incident = TaskProtocolJson.Copy(entry.Incident);
            _incidentReplays.Add(entry.Key, entry.Json);
            _incidents[incident.Id] = incident;
            _incidentHistory.Add(new(_attemptId, incident));
        }
        _incidentBytes += addedBytes;
        if (batch.RunBoundary is "ended" or "aborted") RunBoundary = batch.RunBoundary;
        else if (RunBoundary is not ("ended" or "aborted")) RunBoundary = batch.RunBoundary;
        if (logs.HasGap)
            foreach (var key in _selected.Where(key => _effective[key].Status is "pending" or "running").ToArray())
                _effective[key] = _effective[key] with { Status = "unknown", ReasonCode = "logs.gap", ReasonText = null };
        Revision++;
    }

    internal void FinishAttempt(string lifecycle)
    {
        foreach (string key in _selected)
        {
            var state = _effective[key];
            if (state.Status is "pending" or "running")
                _effective[key] = state with { Status = lifecycle == "cancelled" ? "cancelled" : "unknown", ReasonCode = "tasks.no_terminal_evidence", ReasonText = null };
        }
        _hadFailure |= lifecycle is "failed" or "interrupted";
        Revision++;
    }

    private string ReducedStatus(string id)
    {
        string own = _effective.TryGetValue(id, out var result) ? result.Status : "unknown";
        var children = _tasks.Values.Where(t => t.Enabled && t.ParentId == id && t.RequiredForParent).ToArray();
        if (children.Length == 0) return own;
        string[] states = children.Select(t => ReducedStatus(t.Id)).ToArray();
        if (states.All(s => s == "failed")) return "failed";
        if (states.Any(s => s is "failed" or "partial")) return "partial";
        if (own == "failed") return states.Any(s => s is "succeeded" or "skipped") ? "partial" : "failed";
        if (states.Any(s => s is not ("succeeded" or "skipped"))) return "unknown";
        return own;
    }

    internal TaskSummary Summarize(string lifecycle)
    {
        var counts = new[] { "total", "succeeded", "failed", "partial", "skipped", "blocked", "unknown", "pending", "running", "cancelled" }
            .ToDictionary(s => s, _ => 0, StringComparer.Ordinal);
        foreach (var task in _tasks.Values.Where(t => t.Enabled && t.ParentId is null && t.Role == "business" && t.CountsAsUnit))
        { counts["total"]++; counts[ReducedStatus(task.Id)]++; }
        (string tone, string outcome) = lifecycle is "failed" or "interrupted" ? ("bad", "lifecycle_failed")
            : counts["total"] > 0 && counts["failed"] == counts["total"] ? ("bad", "all_failed")
            : counts["failed"] + counts["partial"] > 0 ? ("warn", "partial_failure")
            : lifecycle == "cancelled" ? ("muted", "cancelled")
            : counts["total"] == 0 ? ("muted", "no_tasks")
            : lifecycle != "completed" || counts["succeeded"] + counts["skipped"] != counts["total"] ? ("muted", "incomplete")
            : ("ok", "all_satisfied");
        return new(tone, outcome, counts, _hadFailure && tone == "ok");
    }

    internal TaskRetrySelection SelectRetry(int maximumAttempts, bool cancelled, bool budgetExhausted)
    {
        static TaskRetrySelection Stop(string code) => new("stop", code, [], [], []);
        if (cancelled) return Stop("retry.cancelled");
        if (budgetExhausted || _attemptNumber >= maximumAttempts) return Stop("retry.budget_exhausted");
        var selected = _effective.Values.Where(r => r.Status is "failed" or "blocked").Select(r => r.TaskId).ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0) return Stop("retry.no_verified_candidate");
        var prerequisites = new HashSet<string>(StringComparer.Ordinal);
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        bool changed;
        do
        {
            int before = selected.Count;
            foreach (string key in selected.ToArray())
            {
                string unit = _tasks[key].RetryUnitId;
                var related = _tasks.Values.Where(t => t.Enabled && t.RetryUnitId == unit).Select(t => t.Id).Append(unit).ToArray();
                if (related.Any(id => !selected.Contains(id))) expanded.Add(unit);
                selected.UnionWith(related);
                foreach (string dependency in _tasks[key].Dependencies) { prerequisites.Add(dependency); selected.Add(dependency); }
            }
            changed = before != selected.Count;
        } while (changed);
        if (selected.Any(key => !_tasks[key].Enabled && _tasks[key].Role == "business")) return Stop("retry.disabled_business_dependency");
        // Conditional risks need an explicit host-verifiable condition; no generic implicit approval.
        if (selected.Any(key => _tasks[key].RetryRisk != "safe")) return Stop("retry.risk_not_verified");
        var ordered = new List<string>();
        while (selected.Count > 0)
        {
            var ready = selected.Where(id => !_tasks[id].Dependencies.Any(selected.Contains)).OrderBy(id => _tasks[id].Order).ThenBy(id => id, StringComparer.Ordinal).ToArray();
            TaskProtocolValidation.Require(ready.Length > 0, "retry dependency cycle");
            ordered.AddRange(ready); selected.ExceptWith(ready);
        }
        return new("selective", "retry.unfinished", ordered.ToArray(), ordered.Where(prerequisites.Contains).ToArray(), expanded.Order(StringComparer.Ordinal).ToArray());
    }
}
