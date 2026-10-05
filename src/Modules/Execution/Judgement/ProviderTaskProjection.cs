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
    private readonly JsonArray _progress = new();
    private readonly Queue<long> _progressSequences = new();
    private int _progressBytes;
    private long _lastSequence;
    private bool _diagnosticTruncated;
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
        _reducer = TaskRunReducer.CreateProvider(record, new TaskPlan("provider-execution-v1", plan.PlanId, "provider", provider,
            version, DateTimeOffset.Now, plan.AuthorizationFingerprint, "complete", tasks, []) { SemanticsVersion = "provider-execution-v1" });
        _reducer.BeginAttempt(_attempt, number, tasks.Select(task => task.Id));
    }

    internal bool Accept(PluginProviderEvent item)
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
        { if (old != json) throw new InvalidDataException("provider.conflicting_event"); return false; }
        if (_lastSequence > 0 && item.Sequence != _lastSequence + 1)
            throw new InvalidDataException("provider.event_gap");
        int bytes = System.Text.Encoding.UTF8.GetByteCount(json);
        if (item.Kind == "progress")
        {
            _lastSequence = item.Sequence;
            _replays.Add(item.Sequence, json); _progressSequences.Enqueue(item.Sequence);
            _progress.Add(fact); _progressBytes += bytes;
            while (_progress.Count > 256 || _progressBytes > 128 * 1024)
            {
                _progressBytes -= System.Text.Encoding.UTF8.GetByteCount(_progress[0]!.ToJsonString());
                _progress.RemoveAt(0); _replays.Remove(_progressSequences.Dequeue()); _diagnosticTruncated = true;
            }
            // Only a bounded protocol code is public; project text may contain secrets.
            if (item.Evidence["code"] is JsonValue codeValue
                && codeValue.TryGetValue<string>(out string? code)
                && code is { Length: > 0 and <= 128 }
                && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
                && _diagnostics.Count < 32
                && !_diagnostics.OfType<JsonObject>().Any(d => d["code"]?.GetValue<string>() == code))
            {
                _diagnostics.Add(new JsonObject { ["code"] = code, ["severity"] = "warning" });
                return true;
            }
            return false;
        }
        if (item.Kind is not ("ready" or "cancel_ack" or "task_event" or "completed" or "fault"))
            throw new InvalidDataException("provider.event_kind");
        if (_structured.Count >= 8192 || _evidenceBytes + bytes + 64 > 4 * 1024 * 1024)
            throw new InvalidDataException("provider.evidence_limit");
        string evidenceId = Guid.NewGuid().ToString("N");
        _lastSequence = item.Sequence;
        _replays.Add(item.Sequence, json); _evidenceIds.Add(evidenceId);
        fact["id"] = evidenceId; _structured.Add(fact); _evidenceBytes += bytes + 64;
        if (item.Kind is "ready" or "cancel_ack") return true;
        if (item.Kind == "task_event")
        {
            if (!_reducer.OriginalPlan.Tasks.Any(task => task.Id == item.TaskId)
                || item.Status is not ("running" or "succeeded" or "failed"))
                throw new InvalidDataException("provider.task_identity_or_status");
            if (_engineTasks.TryGetValue(item.TaskId!, out string? previous)
                ? previous != "running" || item.Status == "running" : item.Status != "running")
                throw new InvalidDataException("provider.task_transition");
            _engineTasks[item.TaskId!] = item.Status!;
        }
        if (item.Kind == "completed" && (item.Status != "succeeded"
            || _reducer.OriginalPlan.Tasks.Any(task => _engineTasks.GetValueOrDefault(task.Id) != "succeeded")))
            throw new InvalidDataException("provider.missing_task_terminal");
        var observation = item.Kind == "task_event" ? new TaskObservation
        {
            Id = "native:" + item.Sequence, TaskId = item.TaskId ?? "", ExecutionOrdinal = 1,
            Status = item.Status == "running" ? "running" : "unknown",
            ReasonCode = "provider.engine_" + item.Status, Evidence = [], StructuredEvidenceRefs = [evidenceId],
        } : null;
        _reducer.AcceptProviderFacts(new(_record, _attempt,
            observation is null ? [] : [observation],
            item.Kind == "completed" ? "ended" : item.Kind == "fault" ? "aborted" : "unknown",
            item.Kind is "completed" or "fault" ? [evidenceId] : null), _evidenceIds);
        return true;
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
        ["schemaVersion"] = 1, ["semanticsVersion"] = "provider-execution-v1", ["runId"] = _record, ["pluginId"] = _provider,
        ["revision"] = _reducer.Revision, ["userId"] = _user, ["scriptInstanceId"] = _script,
        ["originalPlan"] = JsonNode.Parse(TaskProtocolJson.Write(_reducer.OriginalPlan)),
        ["lifecycleOutcome"] = _lifecycle, ["engineStatus"] = _engine,
        ["businessVerification"] = "unverified", ["structuredEvidenceVersion"] = 1,
        ["structuredEvidence"] = _structured.DeepClone(),
        ["progressEvidence"] = _progress.DeepClone(), ["diagnosticTruncated"] = _diagnosticTruncated,
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
