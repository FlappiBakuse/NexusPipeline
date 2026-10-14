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
    private string _attempt = "";
    private int _number;
    private readonly bool _framework;
    private string[] _selected = [];
    private readonly JsonArray _attempts = new();
    private readonly Dictionary<string, string> _nativeTasks = new(StringComparer.Ordinal);
    private bool _taskFailed;
    internal IReadOnlyList<string> SelectedTaskIds => _selected.ToArray();
    internal string[] RetryTaskIds() => _taskFailed
        ? _reducer.OriginalPlan.Tasks.OrderBy(task => task.Order).Select(task => task.Id)
            .SkipWhile(id => _engineTasks.GetValueOrDefault(id) != "failed")
            .Where(id => _engineTasks.GetValueOrDefault(id) != "succeeded").ToArray() : [];

    internal void BeginAttempt(int number, IReadOnlyList<string> selected)
    {
        if (selected.Count == 0 || selected.Distinct(StringComparer.Ordinal).Count() != selected.Count
            || !_reducer.OriginalPlan.Tasks.OrderBy(t => t.Order).Select(t => t.Id).Where(selected.Contains).SequenceEqual(selected))
            throw new InvalidDataException("provider.attempt_selection");
        _number = number; _attempt = _record + ":provider:" + number;
        _selected = selected.ToArray(); _lifecycle = "running"; _engine = "running"; _taskFailed = false;
        _lastSequence = 0; _replays.Clear(); _progressSequences.Clear(); _progress.Clear(); _progressBytes = 0;
        _nativeTasks.Clear(); _instanceJudgement = null;
        foreach (string id in _selected) _engineTasks.Remove(id);
        _reducer.BeginAttempt(_attempt, number, _selected);
    }

    internal bool Accept(int number, PluginProviderEvent item)
    {
        if (number != _number) throw new InvalidDataException("provider.stale_attempt");
        return Accept(item);
    }
    private string _lifecycle = "running";
    private string _engine = "running";
    private readonly JsonArray _structured = new();
    private readonly JsonArray _diagnostics = new();
    private readonly JsonArray _progress = new();
    private readonly JsonObject _logSources = new();
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
    private RunAttemptResult? _instanceJudgement;
    internal ProviderTaskProjection(PluginProviderPlan plan, string provider, string version,
        string record, string user, string script, int number)
    {
        _record = record; _provider = provider; _user = user; _script = script;
        _framework = plan.ResultBasis == "framework-task-v1";
        TaskDefinition[] tasks = plan.Tasks.Select(task => new TaskDefinition
        {
            Id = task.Id, SourceKey = task.Id, Name = task.Name, ParentId = null, Role = "business",
            Enabled = true, Order = task.Order, CountsAsUnit = true, RequiredForParent = false,
            RetryUnitId = task.Id, RetryRisk = "unknown", Dependencies = [], Detection = "supported",
        }).ToArray();
        _reducer = TaskRunReducer.CreateProvider(record, new TaskPlan("provider-execution-v1", plan.PlanId, "provider", provider,
            version, DateTimeOffset.Now, plan.AuthorizationFingerprint, "complete", tasks, []) { SemanticsVersion = "provider-execution-v1" });
        BeginAttempt(number, tasks.OrderBy(task => task.Order).Select(task => task.Id).ToArray());
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
        if (item.Kind is "progress" or "log")
        {
            if (item.Evidence["logSource"] is JsonValue sourceValue && sourceValue.TryGetValue<string>(out string? source)
                && source is { Length: > 0 and <= 64 } && source.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
                && (_logSources.ContainsKey(source) || _logSources.Count < 32))
            {
                var metadata = new JsonObject();
                foreach (string key in new[] { "logPath", "fileIdentity", "startByteOffset", "byteOffset", "rotationEpoch", "encoding", "sourceState" })
                    if (item.Evidence[key] is JsonValue field && field.ToJsonString().Length <= 512) metadata[key] = field.DeepClone();
                metadata["sequence"] = item.Sequence; _logSources[source] = metadata;
            }
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
                && !(code.StartsWith("log.", StringComparison.Ordinal) && (code.EndsWith(".complete", StringComparison.Ordinal) || code.EndsWith(".absent", StringComparison.Ordinal)))
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
            if (!_selected.Contains(item.TaskId)
                || item.Status is not ("running" or "succeeded" or "failed"))
                throw new InvalidDataException("provider.task_identity_or_status");
            if (_engineTasks.TryGetValue(item.TaskId!, out string? previous)
                ? previous != "running" || item.Status == "running" : item.Status != "running")
                throw new InvalidDataException("provider.task_transition");
            if (_framework)
            {
                string native = item.Evidence["nativeTaskId"]?.GetValue<string>() ?? "";
                if (!ulong.TryParse(native, out ulong job) || job == 0)
                    throw new InvalidDataException("provider.native_task_identity");
                if (item.Status == "running")
                {
                    if (_nativeTasks.ContainsKey(native) || _selected.TakeWhile(id => id != item.TaskId)
                        .Any(id => _engineTasks.GetValueOrDefault(id) != "succeeded"))
                        throw new InvalidDataException("provider.task_order");
                    _nativeTasks.Add(native, item.TaskId!);
                }
                else if (_nativeTasks.GetValueOrDefault(native) != item.TaskId)
                    throw new InvalidDataException("provider.native_task_identity");
            }
            _engineTasks[item.TaskId!] = item.Status!;
        }
        if (item.Kind == "completed")
        {
            bool succeeded = item.Status == "succeeded" && _selected.All(id => _engineTasks.GetValueOrDefault(id) == "succeeded");
            bool failed = _framework && item.Status == "failed" && item.Evidence["code"]?.GetValue<string>() == "maa.task_failed"
                && _selected.Count(id => _engineTasks.GetValueOrDefault(id) == "failed") == 1
                && !_selected.Any(id => _engineTasks.GetValueOrDefault(id) == "running");
            if (!succeeded && !failed) throw new InvalidDataException("provider.missing_task_terminal");
            _taskFailed = failed;
        }
        var observation = item.Kind == "task_event" ? new TaskObservation
        {
            Id = "native:" + item.Sequence, TaskId = item.TaskId ?? "", ExecutionOrdinal = 1,
            Status = _framework ? item.Status! : item.Status == "running" ? "running" : "unknown",
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
        if (_lifecycle != "running") return;
        _engine = engine;
        _lifecycle = cancelled ? "cancelled" : engine == "succeeded" ? "completed" : "failed";
        _reducer.FinishAttempt(_lifecycle, preservePending: _framework);
        _attempts.Add(new JsonObject { ["attemptId"] = _attempt, ["number"] = _number,
            ["selectedTaskIds"] = new JsonArray(_selected.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["taskResults"] = AttemptResults(), ["stopReason"] = cancelled ? "cancelled" : _taskFailed ? "maa.task_failed" : engine });
    }

    internal void ApplyJudgement(SessionJudge judge)
    {
        if (_framework || !judge.IsConfigured) return;
        string reason = judge.Reason ?? (judge.IsFailure ? "失败关键字已命中" : "完成关键字已命中");
        _instanceJudgement = judge.IsFailure ? RunAttemptResult.Failed(reason, "provider.judge_failed")
            : judge.MarkerOutcome == SessionJudge.JudgeOutcome.Partial ? RunAttemptResult.Partial(reason, "provider.judge_partial")
            : judge.IsMarker ? RunAttemptResult.Success(reason, "provider.judge_succeeded") : null;
        if (_instanceJudgement is not null)
        {
            _instanceJudgement.NotifyText = judge.NotifyText;
            _instanceJudgement.NotifyScreenshotId = judge.NotifyScreenshotId;
        }
    }

    private JsonArray Results()
    {
        var results = JsonNode.Parse(TaskProtocolJson.Write(_reducer.Results))!.AsArray();
        foreach (var result in results.OfType<JsonObject>())
            result["engineStatus"] = _engineTasks.GetValueOrDefault(result["taskId"]!.GetValue<string>(), "not_started");
        return results;
    }

    private JsonArray AttemptResults() => new(Results().OfType<JsonObject>()
        .Where(result => _selected.Contains(result["taskId"]!.GetValue<string>())).Select(result => (JsonNode?)result.DeepClone()).ToArray());

    private JsonArray Attempts()
    {
        var attempts = _attempts.DeepClone().AsArray();
        if (_lifecycle == "running") attempts.Add(new JsonObject { ["attemptId"] = _attempt, ["number"] = _number,
            ["selectedTaskIds"] = new JsonArray(_selected.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()), ["taskResults"] = AttemptResults() });
        return attempts;
    }

    internal JsonObject Snapshot() => new()
    {
        ["resultBasis"] = _framework ? "framework-task-v1" : "legacy-provider", ["schemaVersion"] = 1, ["semanticsVersion"] = "provider-execution-v1", ["runId"] = _record, ["pluginId"] = _provider,
        ["revision"] = _reducer.Revision, ["userId"] = _user, ["scriptInstanceId"] = _script,
        ["originalPlan"] = JsonNode.Parse(TaskProtocolJson.Write(_reducer.OriginalPlan)),
        ["lifecycleOutcome"] = _lifecycle, ["engineStatus"] = _engine,
        ["businessVerification"] = _instanceJudgement?.Status switch {
            "success" => "verified_succeeded", "partial" => "partial", "failed" => "verified_failed", _ => "unverified" },
        ["instanceJudgement"] = _instanceJudgement is null ? null : new JsonObject {
            ["status"] = _instanceJudgement.Status, ["reasonCode"] = _instanceJudgement.ReasonCode, ["scope"] = "instance" },
        ["structuredEvidenceVersion"] = 1,
        ["structuredEvidence"] = _structured.DeepClone(),
        ["progressEvidence"] = _progress.DeepClone(), ["diagnosticTruncated"] = _diagnosticTruncated,
        ["logSources"] = _logSources.DeepClone(),
        ["attemptReports"] = Attempts(),
        ["finalTaskResults"] = Results(),
        ["summary"] = JsonNode.Parse(TaskProtocolJson.Write(_reducer.Summarize(_lifecycle))),
        ["incidents"] = new JsonArray(), ["diagnostics"] = _diagnostics.DeepClone(),
    };

    internal RunAttemptResult Result(string detail)
    {
        if (_lifecycle == "cancelled") return RunAttemptResult.Cancelled(detail);
        if (_instanceJudgement is not null) return _instanceJudgement;
        if (_framework && _taskFailed) return RunAttemptResult.Failed(detail, "provider.tasks_failed");
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
