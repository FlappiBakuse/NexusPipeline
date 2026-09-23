using NexusPipeline.Modules.Configuration.Validation;
using NexusPipeline.Modules.Configuration.Paths;
using NexusPipeline.Modules.Configuration.Snapshots;
using NexusPipeline.Modules.Configuration.Scripting;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Execution;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Queues.Queries;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts.Queries;
using NexusPipeline.Modules.Scripts.Resolution;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Modules.Users.Queries;
using NexusPipeline.Shared.Localization;

namespace NexusPipeline.Host.Composition.Adapters;

internal sealed class TaskQueryProjection(UserQueries users, ScriptQueries scripts, ScriptSpecResolver resolver,
    IScriptConfigGate gate, RunHistoryService history, ExecutionDispatcher execution,
    QueueQueries queues) : ITaskQueryProjection
{
    public object Summaries()
    {
        var latest = history.LatestTasks().ToDictionary(r => (r.UserId, r.ScriptInstanceId));
        var admissions = history.LatestAdmissions().ToDictionary(r => (r.UserId, r.ScriptInstanceId));
        var active = execution.Active.SelectMany(e => e.SnapshotTaskReports()).Where(r => r["lifecycleOutcome"]?.GetValue<string>() == "running")
            .GroupBy(r => r["userId"]?.GetValue<string>() ?? "").ToDictionary(g => g.Key, g => g.Count());
        var specialized = scripts.ListEffective().Where(s => !string.IsNullOrWhiteSpace(s.PluginType)).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        return users.List().Select(user =>
        {
            var bindings = user.Bindings.Where(b => b.Effective.Enabled && specialized.Contains(b.ScriptInstanceId)).Select(binding =>
            {
                var result = latest.GetValueOrDefault((user.Id, binding.ScriptInstanceId));
                var admission = admissions.GetValueOrDefault((user.Id, binding.ScriptInstanceId));
                bool admissionCurrent = admission?.Deleted == false
                    && (result is null || result.Deleted || admission.EndTime >= result.EndTime);
                string tone = result is null || result.Deleted ? "muted" : result.Tone
                    ?? (result.Status == "failed" ? "bad" : result.Status == "partial" ? "warn" : "muted");
                return new { binding.ScriptInstanceId, tone, recordId = result?.Deleted == false ? result.RecordId : null,
                    endTime = result?.EndTime,
                    reason = result is null ? "not_run" : result.Deleted ? "record_deleted" : result.Tone is null ? "legacy_unknown" : "latest",
                    admissionRecordId = admissionCurrent ? admission!.RecordId : null,
                    admissionEndTime = admissionCurrent ? (DateTime?)admission!.EndTime : null,
                    admissionState = admissionCurrent ? admission!.State : null,
                    admissionReason = admissionCurrent ? admission!.ReasonCode : null };
            }).ToArray();
            var worst = bindings.OrderBy(b => b.tone switch { "bad" => 0, "warn" => 1, "muted" => 2, _ => 3 })
                .ThenByDescending(b => b.endTime).ThenBy(b => b.recordId, StringComparer.Ordinal).FirstOrDefault();
            return new { userId = user.Id, tone = worst?.tone ?? "muted", recordId = worst?.recordId,
                activeCount = active.GetValueOrDefault(user.Id),
                reason = bindings.Length == 0 ? "no_bindings" : worst!.reason, bindings };
        }).ToArray();
    }

    public async Task<object> PreviewAsync(string userId, string scriptId, CancellationToken token)
    {
        var user = users.Find(userId);
        var binding = user?.Bindings.SingleOrDefault(b => b.ScriptInstanceId == scriptId);
        var script = scripts.FindDeclaration(scriptId);
        if (binding is null || script is null) return new { error = "config_unavailable" };
        using var lease = gate.TryAcquire(scriptId);
        if (lease is null) return new { error = "configuration_busy" };
        try
        {
            var spec = resolver.Resolve(script, binding.ConfigInputs);
            if (spec.TaskProtocol is null) return new { error = "unsupported_schema" };
            string store = ConfigPaths.StoreDir(scriptId, userId);
            // A file snapshot is stored under its original basename; directory snapshots keep relative paths.
            var metadata = ConfigStoreMetadata.Load(scriptId, userId);
            if (metadata is null || metadata.ConfigLocatorHash != ConfigStoreMetadata.HashLocator(spec.Script.ConfigPath)
                || metadata.ConfigKind is not ("file" or "dir")) return new { error = "config_unavailable" };
            string config = metadata.ConfigKind == "file" ? Path.Combine(store, Path.GetFileName(spec.Script.ConfigPath)) : store;
            if (!Directory.Exists(store)) return new { error = "config_unavailable" };
            var extras = spec.ExtraConfigPaths.Select(path =>
            {
                string saved = ConfigPaths.StoreExtraDir(scriptId, userId, path);
                string file = Path.Combine(saved, Path.GetFileName(path));
                return File.Exists(file) ? file : saved;
            }).ToArray();
            var view = TaskConfigViewFactory.Capture(config, spec.Script.RootPath, extras, spec.TaskProtocol.ReadResources, spec.Script.ConfigPath, spec.ExtraConfigPaths);
            TaskQueueContextFact queueContext = TaskQueueContextResolver.Resolve(
                queues.List().Select(item => item.Queue).ToList(),
                scriptId);
            TaskExecutionContext context = ExecutionCoordinator.CreateTaskExecutionContext(
                script,
                spec,
                userId,
                "preview",
                queueContext.QueueId,
                queueContext.HasFollowingWork);
            var plan = await TaskDiscoveryService.DiscoverAsync(spec.TaskProtocol, view, script.PluginType, spec.PluginVersion,
                userId, scriptId, LocaleContext.Current, true, token,
                executionContext: context,
                scriptRoot: spec.Script.RootPath,
                scriptExecutable: spec.Script.MainExe).ConfigureAwait(false);
            var last = history.LatestTasks().SingleOrDefault(r => r.UserId == userId && r.ScriptInstanceId == scriptId);
            return new { plan, stale = last?.Signature is { } signature && signature != plan.Signature,
                queueContext = new { kind = queueContext.QueueId is null ? "standalone" : "queue",
                    queueId = queueContext.QueueId,
                    hasFollowingWork = queueContext.HasFollowingWork },
                revision = plan.PlanId, configState = "snapshot", readOnly = true };
        }
        catch (OperationCanceledException) { return new { error = "cancelled" }; }
        catch (TimeoutException) { return new { error = "timeout" }; }
        catch (Exception ex)
        { return new { error = TaskAssessmentFailure.Code(ex.Message) }; }
    }
}
