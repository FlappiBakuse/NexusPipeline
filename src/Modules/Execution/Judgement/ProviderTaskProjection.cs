using System.Text.Json.Nodes;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Plugin.Abstractions;

namespace NexusPipeline.Modules.Execution.Judgement;

/// <summary>Maps authenticated structured worker facts into the existing task reducer.</summary>
internal sealed class ProviderTaskProjection
{
    private readonly TaskRunReducer _reducer;
    private readonly string _record;
    private readonly string _provider;
    private readonly string _user;
    private readonly string _script;
    private readonly string _attempt;
    private readonly int _number;
    private string _lifecycle = "running";
    private string _engine = "running";
    private readonly JsonArray _structured = new();
    private readonly JsonArray _diagnostics = new();
    private readonly HashSet<string> _evidenceIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _engineTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _replays = new();
    private int _evidenceBytes;
    private string _frameworkVersion = "";
    private string _projectVersion = "";
    internal ProviderTaskProjection(PluginProviderPlan plan, string provider, string version,
        string record, string user, string script, int number)
    {
        _record = record; _provider = provider; _user = user; _script = script;
        _number = number; _attempt = record + ":provider:" + number;
        TaskDefinition[] tasks = plan.Tasks.Select(task => new TaskDefinition
        {
            Id = task.Id, SourceKey = task.Id, Name = task.Name, ParentId = null, Role = "business",
            Enabled = true, Order = task.Order, CountsAsUnit = true, RequiredForParent = false,
            RetryUnitId = task.Id, RetryRisk = "unknown", Dependencies = [], Detection = "supported",
        }).ToArray();
        _reducer = new(record, new TaskPlan("0.1.0", plan.PlanId, "provider", provider,
            version, DateTimeOffset.Now, plan.AuthorizationFingerprint, "complete", tasks, []));
        _reducer.BeginAttempt(_attempt, number, tasks.Select(task => task.Id));
    }

    internal void Accept(PluginProviderEvent item)
    {
        if (_lifecycle != "running") throw new InvalidDataException("provider.event_after_terminal");
        // Store only protocol facts. Arbitrary project output and secrets never become evidence.
        var fact = new JsonObject { ["providerId"] = _provider, ["recordId"] = _record,
            ["attemptId"] = _attempt, ["sourceSequence"] = item.Sequence, ["kind"] = item.Kind,
            ["taskId"] = item.TaskId, ["status"] = item.Status };
        if (item.Kind == "ready")
        {
            _frameworkVersion = item.Evidence["nativeVersion"]?.GetValue<string>() ?? "";
            _projectVersion = item.Evidence["projectVersion"]?.GetValue<string>() ?? "";
        }
        fact["frameworkVersion"] = _frameworkVersion; fact["projectVersion"] = _projectVersion;
        foreach (string key in new[] { "sessionId", "nativeTaskId", "nativeVersion", "task_id", "node_id", "reco_id", "action_id", "code" })
            if (item.Evidence[key] is JsonValue value && value.ToJsonString().Length <= 512) fact[key] = value.DeepClone();
        string json = fact.ToJsonString();
        if (_replays.TryGetValue(item.Sequence, out string? old))
        { if (old != json) throw new InvalidDataException("provider.conflicting_event"); return; }
        if (_replays.Count > 0 && item.Sequence != _replays.Keys.Max() + 1)
            throw new InvalidDataException("provider.event_gap");
        int bytes = System.Text.Encoding.UTF8.GetByteCount(json);
        if (_structured.Count >= 2048 || _evidenceBytes + bytes > 256 * 1024)
            throw new InvalidDataException("provider.evidence_limit");
        string evidenceId = Guid.NewGuid().ToString("N");
        _replays.Add(item.Sequence, json); _evidenceIds.Add(evidenceId);
        fact["id"] = evidenceId; _structured.Add(fact); _evidenceBytes += bytes;
        if (item.Kind == "progress")
        {
            // Only a bounded protocol code is public; project text may contain secrets.
            if (item.Evidence["code"] is JsonValue codeValue
                && codeValue.TryGetValue<string>(out string? code)
                && code is { Length: > 0 and <= 128 }
                && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
                && _diagnostics.Count < 32
                && !_diagnostics.OfType<JsonObject>().Any(d => d["code"]?.GetValue<string>() == code))
                _diagnostics.Add(new JsonObject { ["code"] = code, ["severity"] = "warning",
                    ["structuredEvidenceRefs"] = new JsonArray(evidenceId) });
            return;
        }
        if (item.Kind is "ready" or "cancel_ack") return;
        if (item.Kind == "task_event")
        {
            if (!_reducer.OriginalPlan.Tasks.Any(task => task.Id == item.TaskId)
                || item.Status is not ("running" or "succeeded" or "failed"))
                throw new InvalidDataException("provider.task_identity_or_status");
            _engineTasks[item.TaskId!] = item.Status!;
        }
        var observation = item.Kind == "task_event" ? new TaskObservation
        {
            Id = "native:" + item.Sequence, TaskId = item.TaskId ?? "", ExecutionOrdinal = 1,
            Status = item.Status == "running" ? "running" : "unknown",
            ReasonCode = "provider.engine_" + item.Status, Evidence = [], StructuredEvidenceRefs = [evidenceId],
        } : null;
        _reducer.AcceptStructured(new TaskObservationBatch
        {
            ProtocolVersion = "0.1.0", Type = "observation", RunId = _record, AttemptId = _attempt,
            Observations = observation is null ? [] : [observation],
            RunBoundary = item.Kind == "completed" ? "ended" : item.Kind == "fault" ? "aborted" : "unknown",
            BoundaryEvidence = [], StructuredEvidenceVersion = 1,
            BoundaryStructuredEvidenceRefs = item.Kind is "completed" or "fault" ? [evidenceId] : null, Diagnostics = [],
        }, _evidenceIds);
    }

    internal void Finish(string engine, bool cancelled)
    {
        _engine = engine;
        _lifecycle = cancelled ? "cancelled" : engine == "succeeded" ? "completed" : "failed";
        _reducer.FinishAttempt(_lifecycle);
    }

    private JsonArray Results()
    {
        var results = JsonNode.Parse(TaskProtocolJson.Write(_reducer.Results))!.AsArray();
        foreach (var result in results.OfType<JsonObject>())
            result["engineStatus"] = _engineTasks.GetValueOrDefault(result["taskId"]!.GetValue<string>(), "not_started");
        return results;
    }

    internal JsonObject Snapshot() => new()
    {
        ["schemaVersion"] = 1, ["runId"] = _record, ["pluginId"] = _provider,
        ["revision"] = _reducer.Revision, ["userId"] = _user, ["scriptInstanceId"] = _script,
        ["originalPlan"] = JsonNode.Parse(TaskProtocolJson.Write(_reducer.OriginalPlan)),
        ["lifecycleOutcome"] = _lifecycle, ["engineStatus"] = _engine,
        ["businessVerification"] = "unverified", ["structuredEvidenceVersion"] = 1,
        ["structuredEvidence"] = _structured.DeepClone(),
        ["attemptReports"] = new JsonArray(new JsonObject { ["attemptId"] = _attempt, ["number"] = _number,
            ["selectedTaskIds"] = new JsonArray(_reducer.OriginalPlan.Tasks.Select(task => (JsonNode?)JsonValue.Create(task.Id)).ToArray()),
            ["taskResults"] = Results() }),
        ["finalTaskResults"] = Results(),
        ["summary"] = JsonNode.Parse(TaskProtocolJson.Write(_reducer.Summarize(_lifecycle))),
        ["incidents"] = new JsonArray(), ["diagnostics"] = _diagnostics.DeepClone(),
    };

    internal RunAttemptResult Result(string detail)
    {
        if (_lifecycle == "cancelled") return RunAttemptResult.Cancelled(detail);
        if (_engine != "succeeded") return RunAttemptResult.Fatal(detail, "provider.engine_failed");
        return _reducer.Summarize(_lifecycle).Outcome switch
        {
            "all_satisfied" => RunAttemptResult.Success(detail, "provider.succeeded"),
            "all_failed" => RunAttemptResult.Failed(detail, "provider.tasks_failed"),
            "partial_failure" => RunAttemptResult.Partial(detail, "provider.partial"),
            _ => new RunAttemptResult { Status = "unverified", Reason = "流程已结束 · 有未核验项", ReasonCode = "tasks.unverified", IsFatal = true },
        };
    }
}
