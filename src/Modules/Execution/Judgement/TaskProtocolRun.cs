using System.Text.Json.Nodes;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Recovery;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Execution.Monitoring;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts.Contracts;

namespace NexusPipeline.Modules.Execution.Judgement;

/// <summary>Connects protocol phases to one existing run lease. Games remain entirely in plugin scripts.</summary>
internal sealed class TaskProtocolRun
{
    private readonly ResolvedScriptSpec _spec;
    private readonly TaskProtocolDescriptor _protocol;
    private readonly string _runId;
    private readonly string _userId;
    private readonly string _journalDirectory;
    private readonly TaskExecutionContext? _executionContext;
    private readonly object _gate = new();
    private readonly List<JsonObject> _attempts = new();
    private readonly List<TaskDiagnostic> _diagnostics = new();
    private readonly Dictionary<(string Attempt, string Source, int Epoch, long Sequence), string> _evidenceLines = new();
    private TaskRunReducer? _reducer;
    private TaskSelectionTransaction? _transaction;
    private TaskLogBuffer _logs = new();
    private TaskConfigView? _view;
    private string[] _selected = [];
    private string _attemptId = "";
    private int _attemptNumber;
    private bool _protocolFailed;
    private bool _runtimeIdentityChanged;
    private bool _restrictedRuntime;
    private string _engineStatus = "not_started";
    private string _lifecycle = "not_started";
    private TaskPlan? _expectedRetryPlan;
    private TaskPlan? _admissionPlan;
    private TaskAdmissionBlockedException? _admissionBlocked;
    private RunAttemptResult? _lastCompletedAttemptResult;
    private string _terminationReason = "none";
    private JsonObject? _cursorState;
    private long _revision;
    internal Action<JsonObject>? Changed { get; set; }
    internal bool HasTerminalBoundary
    {
        get { lock (_gate) return _reducer?.RunBoundary is "ended" or "aborted"; }
    }
    private void Publish() { _revision++; var snapshot = Snapshot(); if (snapshot is not null) Changed?.Invoke(snapshot); }

    internal void SetTerminationReason(string reason) { lock (_gate) { if (_terminationReason == "none") _terminationReason = reason; } }
    internal void SetFinalLifecycleFailure(bool cancelled)
    {
        lock (_gate)
        {
            _lifecycle = cancelled ? "cancelled" : "failed";
            if (_attempts.Count > 0) _attempts[^1]["lifecycleOutcome"] = _lifecycle;
            Publish();
        }
    }

    internal TaskProtocolRun(ResolvedScriptSpec spec, string runId, string userId, string? journalDirectory = null,
        TaskExecutionContext? executionContext = null)
    {
        _spec = spec; _protocol = spec.TaskProtocol! with { Localization = spec.TaskProtocol!.Localization is { } texts ? TaskProtocolJson.Copy(texts) : null }; _runId = runId; _userId = userId;
        _executionContext = executionContext;
        _journalDirectory = journalDirectory ?? Path.Combine(ConfigPaths.WorkDir(spec.Script.Id, userId), "task-selection");
    }

    private TaskConfigView Capture() => TaskConfigViewFactory.Capture(_spec.Script.ConfigPath,
        _spec.Script.RootPath, _spec.ExtraConfigPaths, _protocol.ReadResources, _spec.Script.ConfigPath, _spec.ExtraConfigPaths);

    internal bool IsAdmissionBlocked
    {
        get { lock (_gate) return _admissionBlocked is not null; }
    }

    internal bool AdmissionBlockedBeforeAttempt
    {
        get { lock (_gate) return _admissionBlocked is not null && _reducer is null; }
    }

    /// <summary>Runs the read-only 1.2 assessment before an execution attempt is created.</summary>
    internal async Task PreflightAsync(CancellationToken token)
    {
        var view = Capture();
        var plan = await TaskDiscoveryService.DiscoverAsync(_protocol, view, _spec.Script.PluginType, _spec.PluginVersion,
            _userId, _spec.Script.Id, "zh-CN", false, token,
            _executionContext is { } context ? context with { Trigger = "pre_launch" } : null,
            _spec.Script.RootPath, _spec.Script.MainExe).ConfigureAwait(false);
        if (plan.CurrentReadiness?.State == "blocked" && plan.ConfigAssessment is { } assessment)
        {
            lock (_gate)
            {
                _admissionPlan = plan;
                _admissionBlocked = new TaskAdmissionBlockedException(plan.CurrentReadiness, assessment);
            }
            return;
        }
            view.VerifyUnchanged();
        if (plan.Coverage == "unsupported") throw new InvalidDataException("unsupported_schema: cannot establish original task plan");
    }

    internal async Task BeginAsync(int number, CancellationToken token)
    {
        var view = Capture();
        var plan = await TaskDiscoveryService.DiscoverAsync(_protocol, view, _spec.Script.PluginType, _spec.PluginVersion,
            _userId, _spec.Script.Id, "zh-CN", false, token,
            _executionContext is { } context ? context with { Trigger = "pre_launch" } : null,
            _spec.Script.RootPath, _spec.Script.MainExe).ConfigureAwait(false);
        if (plan.CurrentReadiness?.State == "blocked" && plan.ConfigAssessment is { } assessment)
        {
            lock (_gate)
            {
                _admissionPlan = plan;
                _admissionBlocked = new TaskAdmissionBlockedException(plan.CurrentReadiness, assessment);
            }
            throw _admissionBlocked;
        }
        if (plan.Coverage == "unsupported") throw new InvalidDataException("unsupported_schema: cannot establish original task plan");
        if (_reducer is null)
        {
            _restrictedRuntime = _spec.Script.PluginType is "oknte" or "okww"
                && plan.Diagnostics.Any(d => d.Code == "okscript.runtime_restricted")
                && plan.Tasks.Length == 1 && plan.Tasks[0].SourceKey == "runtime_unverified"
                && plan.Tasks[0].Detection == "unsupported";
            _reducer = new(_runId, plan);
            _selected = plan.Tasks.Where(t => t.Enabled).Select(t => t.Id).ToArray();
            var metadata = ConfigSessionMark.FromScript(_spec.Script, _spec.ProfileHash, _spec.PluginVersion, _spec.ExtraConfigPaths);
            _transaction = TaskSelectionTransaction.Freeze(_journalDirectory, view, plan.SelectionFields, owner: new ConfigSessionMark
            {
                ScriptId = _spec.Script.Id, UserId = _userId, ConfigPath = _spec.Script.ConfigPath,
                LaunchExe = metadata.LaunchExe, WorkingDirectory = metadata.WorkingDirectory, ConfigKind = metadata.ConfigKind,
            });
        }
        else if (_expectedRetryPlan is null || !SameTasks(plan, _expectedRetryPlan))
            throw new InvalidDataException("configuration_conflict: pre-run changed retry task identity or selection");
        _view = view;
        _attemptId = _runId + ":" + number;
        _attemptNumber = number;
        lock (_gate)
        {
            _logs = new(); _cursorState = null; _terminationReason = "none"; _lifecycle = "running"; _reducer.BeginAttempt(_attemptId, number, _selected);
        }
        Publish();
    }

    private static bool SameTasks(TaskPlan left, TaskPlan right) =>
        TaskProtocolJson.Write(new { left.Coverage, Tasks = left.Tasks.Select(t => t with { Name = "", NameText = null }), left.SelectionFields, left.BehaviorSignature }) ==
        TaskProtocolJson.Write(new { right.Coverage, Tasks = right.Tasks.Select(t => t with { Name = "", NameText = null }), right.SelectionFields, right.BehaviorSignature });

    internal void Append(string source, string text, bool newEpoch = false)
    {
        lock (_gate) _logs.Append(source, text, newEpoch);
    }

    internal async Task<JudgeScriptResult> ObserveAsync(bool final, CancellationToken token)
    {
        try
        {
            if (!VerifyRuntimeIdentity()) return new JudgeScriptResult { JudgeError = "runtime_identity_changed" };
            // A new official release may use the base lifecycle, but the old
            // interpreter must not turn its logs into verified task outcomes.
            if (_restrictedRuntime) return new JudgeScriptResult();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            bool more;
            do
            {
            TaskLogBatch batch;
            object accepted;
            string terminationReason;
            lock (_gate)
            {
                if (final)
                {
                    _logs.Append("stdout", "", final: true);
                    _logs.Append("file", "", final: true);
                }
                batch = _logs.Peek();
                more = _logs.HasMoreAfter(batch);
                terminationReason = _terminationReason;
                accepted = _reducer!.AcceptedResults.ToDictionary(r => r.TaskId, r => new { r.ExecutionOrdinal, r.Status }, StringComparer.Ordinal);
            }
            var observation = await TaskProtocolScriptRunner.ExecuteAsync<TaskObservationBatch>(_protocol.ObserveScript,
                new { protocolVersion = _protocol.Version, phase = "observe", runId = _runId, attemptId = _attemptId,
                    attemptNumber = _attemptNumber, originalPlan = _reducer!.OriginalPlan, attemptTaskIds = _selected,
                    acceptedState = accepted, adapterState = _cursorState, logBatch = batch, isFinalCall = final && !more, terminationReason = final ? terminationReason : "none" },
                _view!.ReadConfig, _view!.ReadResource, false, deadline.Token).ConfigureAwait(false);
            lock (_gate)
            {
                string previousBoundary = _reducer!.RunBoundary;
                _reducer!.Accept(observation, batch);
                var evidence = observation.Observations.SelectMany(o => o.Evidence).Concat(observation.BoundaryEvidence)
                    .Concat((observation.Incidents ?? []).SelectMany(i => i.Evidence))
                    .Select(e => (e.SourceId, e.Epoch, e.Sequence)).ToHashSet();
                foreach (var line in batch.Records.Where(l => evidence.Contains((l.SourceId, l.Epoch, l.Sequence))))
                    if (_evidenceLines.Count < 8192) _evidenceLines.TryAdd((_attemptId, line.SourceId, line.Epoch, line.Sequence),
                        line.Text.Length <= 1024 ? line.Text : line.Text[..1024]);
                foreach (var diagnostic in observation.Diagnostics)
                    if (_diagnostics.Count < 128 && !_diagnostics.Contains(diagnostic)) _diagnostics.Add(diagnostic);
                _cursorState = observation.CursorState is null ? null : (JsonObject)observation.CursorState.DeepClone();
                _logs.Acknowledge(batch);
                if (final || batch.HasGap || _reducer.RunBoundary != previousBoundary
                    || observation.Observations.Length > 0 || (observation.Incidents?.Length ?? 0) > 0
                    || observation.Diagnostics.Length > 0)
                    Publish();
                if (!more && (_reducer!.RunBoundary is "ended" or "aborted" || final))
                    return new JudgeScriptResult { Status = "partial", Reason = "tasks.observation_complete" };
            }
            } while (final && more);
            return new JudgeScriptResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_gate)
            {
                _protocolFailed = true;
                if (_diagnostics.Count < 128) _diagnostics.Add(new("protocol_error", "Task observation rejected; no cursor was acknowledged."));
            }
            return new JudgeScriptResult { JudgeError = "protocol_error: " + ex.GetType().Name };
        }
    }

    private bool VerifyRuntimeIdentity()
    {
        try
        {
            if (_restrictedRuntime) _view?.VerifyRestrictedRuntimeUnchanged(_protocol.ReadResources);
            else _view?.VerifyPinnedResourcesUnchanged();
            return !_runtimeIdentityChanged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            lock (_gate)
            {
                if (!_runtimeIdentityChanged && _diagnostics.Count < 128)
                    _diagnostics.Add(new("runtime_identity_changed", "Pinned runtime resources changed or became unreadable; later evidence and retries are rejected."));
                _runtimeIdentityChanged = true;
                _protocolFailed = true;
            }
            return false;
        }
    }

    internal RunAttemptResult Finish(RunAttemptResult processResult, int number)
    {
        VerifyRuntimeIdentity();
        lock (_gate)
        {
            _engineStatus = processResult.Status switch
            {
                "success" => "succeeded",
                "failed" or "blocked" => "failed",
                "cancelled" => "cancelled",
                _ => "unknown",
            };
            if (_reducer is null) return processResult;
            if (_attemptNumber != number)
            {
                // A retry can be rejected by the last pre-launch assessment after
                // an earlier attempt already produced business facts. Keep that
                // attempt's outcome; the rejection is reported separately below.
                if (_admissionBlocked is not null && _attempts.Count > 0 && _lastCompletedAttemptResult is { } previous)
                {
                    Publish();
                    return new RunAttemptResult
                    {
                        Status = previous.Status, Reason = previous.Reason, ReasonCode = previous.ReasonCode,
                        ReasonArgs = new(previous.ReasonArgs, StringComparer.Ordinal), IsFatal = true,
                        NotifyText = previous.NotifyText, NotifyScreenshotId = previous.NotifyScreenshotId,
                    };
                }
                _lifecycle = processResult.Status == "cancelled" ? "cancelled" : "failed";
                Publish(); return processResult;
            }
            _lifecycle = processResult.Status == "cancelled" ? "cancelled"
                : _runtimeIdentityChanged || processResult.Status == "failed" || _reducer!.RunBoundary == "aborted" || _terminationReason == "stall" ? "failed" : "completed";
            _reducer!.FinishAttempt(_lifecycle);
            var summary = _reducer.Summarize(_lifecycle);
            _attempts.Add(new JsonObject
            {
                ["attemptId"] = _attemptId, ["number"] = _attemptNumber,
                ["selectedTaskIds"] = JsonNode.Parse(TaskProtocolJson.Write(_selected)),
                ["lifecycleOutcome"] = _lifecycle,
                ["taskResults"] = ResultsJson(_reducer.Results.Where(r => r.LastAttemptId == _attemptId)),
            });
            Publish();
            RunAttemptResult outcome = processResult.Status == "cancelled" || processResult.IsFatal ? processResult
                : _runtimeIdentityChanged ? RunAttemptResult.Failed("Runtime identity changed", "tasks.runtime_identity_changed")
                : summary.Tone == "bad" ? RunAttemptResult.Failed(summary.Outcome, "tasks." + summary.Outcome)
                : summary.Tone == "ok" && summary.Counts["succeeded"] > 0
                    ? RunAttemptResult.Success("tasks.all_satisfied", "tasks.all_satisfied")
                : summary.Outcome == "no_tasks" || summary.Tone == "ok"
                    ? new RunAttemptResult { Status = "skipped", Reason = "tasks.no_execution_required", ReasonCode = "tasks.no_execution_required" }
                : summary.Tone == "warn" ? RunAttemptResult.Partial(summary.Outcome, "tasks.partial_failure")
                : new RunAttemptResult { Status = "unverified", Reason = "流程已结束 · 有未核验项", ReasonCode = "tasks.unverified", IsFatal = true };
            _lastCompletedAttemptResult = outcome;
            return outcome;
        }
    }

    private sealed record RetryOutput(string ProtocolVersion, string Type, string Decision, string ReasonCode,
        string[] IncludedTaskIds, string[] PrerequisiteTaskIds, string[] ExpandedUnitIds, TaskConfigPatch[] FilePatches)
    {
        public JsonObject? ReasonText { get; init; }
    }

    internal async Task<bool> PrepareRetryAsync(int maximum, bool cancelled, bool budgetExpired, CancellationToken token)
    {
        if (_restrictedRuntime)
        {
            SaveRetry(new("stop", "retry.version_restricted", [], [], []));
            return false;
        }
        TaskRetrySelection safe;
        lock (_gate) safe = _reducer!.SelectRetry(maximum, cancelled, budgetExpired);
        if (_protocolFailed || safe.Decision == "stop") { SaveRetry(safe); return false; }
        var view = Capture();
        var proposed = await TaskProtocolScriptRunner.ExecuteAsync<RetryOutput>(_protocol.RetryScript,
            new { protocolVersion = _protocol.Version, phase = "retry", runId = _runId, attemptId = _attemptId,
                originalPlan = _reducer!.OriginalPlan, taskStates = _reducer.Results.ToDictionary(r => r.TaskId, r => r.Status),
                attemptsUsed = _attemptNumber, maxAttempts = maximum, cancelled, budgetExhausted = budgetExpired,
                configResources = view.ConfigResources }, view.ReadConfig, view.ReadResource, false, token).ConfigureAwait(false);
        if (proposed.ProtocolVersion != _protocol.Version || proposed.Type != "retry") throw new InvalidDataException("protocol_error: retry envelope");
        TaskDisplaySnapshot.ValidateReference(proposed.ReasonText, _protocol.Version);
        if (proposed.Decision == "stop")
        {
            if (proposed.IncludedTaskIds.Length + proposed.PrerequisiteTaskIds.Length + proposed.ExpandedUnitIds.Length + proposed.FilePatches.Length != 0)
                throw new InvalidDataException("protocol_error: stop includes actions");
            SaveRetry(new("stop", proposed.ReasonCode, [], [], []), proposed.ReasonText); return false;
        }
        if (proposed.Decision != "selective" || !proposed.IncludedTaskIds.SequenceEqual(safe.IncludedTaskIds)
            || !proposed.PrerequisiteTaskIds.Order().SequenceEqual(safe.PrerequisiteTaskIds.Order())
            || !proposed.ExpandedUnitIds.Order().SequenceEqual(safe.ExpandedUnitIds.Order()))
            throw new InvalidDataException("protocol_error: unsafe retry scope");
        token.ThrowIfCancellationRequested();
        _transaction!.Apply(view, proposed.FilePatches);
        var changed = Capture();
        var plan = await TaskDiscoveryService.DiscoverAsync(_protocol, changed, _spec.Script.PluginType, _spec.PluginVersion,
            _userId, _spec.Script.Id, "zh-CN", false, token,
            _executionContext is { } context ? context with { Trigger = "retry" } : null,
            _spec.Script.RootPath, _spec.Script.MainExe).ConfigureAwait(false);
        if (plan.CurrentReadiness?.State == "blocked" && plan.ConfigAssessment is { } assessment)
        {
            lock (_gate)
            {
                _admissionPlan = plan;
                _admissionBlocked = new TaskAdmissionBlockedException(plan.CurrentReadiness, assessment);
            }
            SaveRetry(new("stop", "tasks.admission_blocked", [], [], []), proposed.ReasonText);
            return false;
        }
        var original = _reducer.OriginalPlan;
        var expected = original with { Tasks = original.Tasks.Select(t => t with { Enabled = safe.IncludedTaskIds.Contains(t.Id, StringComparer.Ordinal) }).ToArray() };
        if (!SameTasks(plan, expected)) throw new InvalidDataException("configuration_conflict: patched selection does not match safe retry");
        _expectedRetryPlan = plan; _selected = safe.IncludedTaskIds;
        SaveRetry(safe, proposed.ReasonText); return true;
    }

    private void SaveRetry(TaskRetrySelection selection, JsonObject? reasonText = null)
    {
        if (_attempts.Count == 0) return;
        _attempts[^1]["retryDecision"] = JsonNode.Parse(TaskProtocolJson.Write(new
        { selection.Decision, selection.ReasonCode, selection.IncludedTaskIds, selection.ExpandedUnitIds }));
        if (reasonText is not null) _attempts[^1]["retryDecision"]!["reasonText"] = reasonText.DeepClone();
        Publish();
    }

    internal string? Restore()
    {
        try { _transaction?.Restore(); _transaction?.Complete(); Publish(); return null; }
        catch (Exception)
        {
            _diagnostics.Add(new("recovery_conflict", "Task selection could not be restored; configuration journal retained."));
            return "recovery_conflict";
        }
    }

    private static JsonArray ResultsJson(IEnumerable<TaskEffectiveResult> results) => (JsonArray)JsonNode.Parse(TaskProtocolJson.Write(
        results.Select(r => new { r.TaskId, r.Status, r.ReasonCode, r.ReasonText, r.LastAttemptId, r.Evidence })))!;

    /// <summary>
    /// A task report is an immutable historical snapshot once the run is no
    /// longer active. Its readiness assessment remains useful for explaining
    /// the decision that was made, but it must not be presented as proof of
    /// the current configuration or environment. Mark only the serialized
    /// report snapshot stale; fresh discovery plans keep their current status.
    /// </summary>
    private static TaskPlan HistoricalPlan(TaskPlan plan) => plan.CurrentReadiness is { } readiness
        ? plan with { CurrentReadiness = readiness with { Stale = true } }
        : plan;

    private static TaskReadiness HistoricalReadiness(TaskReadiness readiness) => readiness with { Stale = true };

    internal JsonObject? Snapshot()
    {
        lock (_gate)
        {
            if (_reducer is null && _admissionPlan is { } blockedPlan && _admissionBlocked is { } blocked)
            {
                var admissionOnly = new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["runId"] = _runId,
                    ["pluginId"] = _spec.Script.PluginType,
                    ["revision"] = _revision,
                    ["userId"] = _userId,
                    ["scriptInstanceId"] = _spec.Script.Id,
                    ["originalPlan"] = JsonNode.Parse(TaskProtocolJson.Write(HistoricalPlan(blockedPlan))),
                    ["lifecycleOutcome"] = "not_started",
                    ["engineStatus"] = "not_started",
                    ["attemptReports"] = new JsonArray(),
                    ["finalTaskResults"] = new JsonArray(),
                    ["incidents"] = new JsonArray(),
                    ["diagnostics"] = JsonNode.Parse(TaskProtocolJson.Write(_diagnostics)),
                    ["admissionBlocked"] = new JsonObject
                    {
                        ["reasonCode"] = "tasks.admission_blocked",
                        ["message"] = blocked.Message,
                        ["readiness"] = JsonNode.Parse(TaskProtocolJson.Write(HistoricalReadiness(blocked.Readiness))),
                        ["configAssessment"] = JsonNode.Parse(TaskProtocolJson.Write(blocked.Assessment)),
                    },
                };
                return admissionOnly;
            }
            if (_reducer is null) return null;
            var attempts = new JsonArray(_attempts.Select(a => a.DeepClone()).ToArray());
            if (_lifecycle == "running") attempts.Add(new JsonObject
            {
                ["attemptId"] = _attemptId, ["number"] = _attemptNumber,
                ["selectedTaskIds"] = JsonNode.Parse(TaskProtocolJson.Write(_selected)),
                ["lifecycleOutcome"] = "running",
                ["taskResults"] = ResultsJson(_reducer.Results.Where(r => r.LastAttemptId == _attemptId)),
            });
            var report = new JsonObject
            {
                ["schemaVersion"] = 1, ["runId"] = _runId, ["pluginId"] = _spec.Script.PluginType,
                ["revision"] = _revision, ["userId"] = _userId, ["scriptInstanceId"] = _spec.Script.Id,
                ["originalPlan"] = JsonNode.Parse(TaskProtocolJson.Write(
                    _lifecycle == "running" ? _reducer.OriginalPlan : HistoricalPlan(_reducer.OriginalPlan))),
                ["lifecycleOutcome"] = _lifecycle,
                ["engineStatus"] = _engineStatus,
                ["attemptReports"] = attempts,
                ["finalTaskResults"] = ResultsJson(_reducer.Results),
                ["incidents"] = JsonNode.Parse(TaskProtocolJson.Write(_reducer.IncidentHistory)),
                ["summary"] = JsonNode.Parse(TaskProtocolJson.Write(_reducer.Summarize(_lifecycle))),
                ["diagnostics"] = JsonNode.Parse(TaskProtocolJson.Write(_diagnostics)),
                ["evidenceLines"] = JsonNode.Parse(TaskProtocolJson.Write(_evidenceLines.Select(p => new
                { attemptId = p.Key.Attempt, sourceId = p.Key.Source, epoch = p.Key.Epoch, sequence = p.Key.Sequence, text = p.Value }))),
            };
            // Include retry-admission diagnostics before freezing the localized
            // display snapshot so newly introduced reasonText references are
            // resolvable in the final report as well.
            if (_admissionBlocked is { } retryBlocked && _admissionPlan is not null)
            {
                report["admissionBlocked"] = new JsonObject
                {
                    ["reasonCode"] = "tasks.admission_blocked",
                    ["message"] = retryBlocked.Message,
                    ["readiness"] = JsonNode.Parse(TaskProtocolJson.Write(HistoricalReadiness(retryBlocked.Readiness))),
                    ["configAssessment"] = JsonNode.Parse(TaskProtocolJson.Write(retryBlocked.Assessment)),
                };
            }
            if (_protocol.Localization is { } localization)
            {
                IEnumerable<JsonObject?> References(JsonNode? node)
                {
                    if (node is JsonObject obj)
                        foreach (var (key, value) in obj)
                            if (key is "nameText" or "reasonText") yield return value as JsonObject;
                            else if (key != "displaySnapshot") foreach (var nested in References(value)) yield return nested;
                    if (node is JsonArray array) foreach (var item in array) foreach (var nested in References(item)) yield return nested;
                }
                var display = (localization with { PluginId = _spec.Script.PluginType, PluginVersion = _spec.PluginVersion }).Select(References(report));
                report["displaySnapshot"] = JsonNode.Parse(TaskProtocolJson.Write(display));
            }
            return report;
        }
    }
}
