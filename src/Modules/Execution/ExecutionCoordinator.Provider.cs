using System.Diagnostics;
using NexusPipeline.ControlPlane.Http;
using NexusPipeline.Modules.Configuration.Exchange;
using NexusPipeline.Modules.Execution.Judgement;
using NexusPipeline.Modules.Execution.Monitoring;
using NexusPipeline.Modules.Execution.Runtime;
using NexusPipeline.Modules.Execution.Targets;
using NexusPipeline.Modules.History;
using NexusPipeline.Modules.Plugins.Contracts;
using NexusPipeline.Modules.Scripts.Contracts;
using NexusPipeline.Modules.Scripts;
using NexusPipeline.Modules.Scripts.Validation;
using NexusPipeline.Modules.Users.Contracts;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Windows;
using NexusPipeline.Shared.Logging;
using NexusPipeline.Modules.Configuration.Paths;

namespace NexusPipeline.Modules.Execution;

internal sealed partial class ExecutionCoordinator : RunSession
{
    private async Task<RunAttemptResult> RunProviderAttemptAsync(RunAttempt attempt)
    {
        var descriptor = _executionProviders?.ResolveExecutionProvider(_script.ExecutionProviderId);
        var plan = _resolvedSpec?.ProviderPlan;
        if (descriptor is null || plan is null || descriptor.PluginVersion != _resolvedSpec?.PluginVersion)
            return RunAttemptResult.Fatal("执行 provider 或冻结计划不可用", "provider.unavailable");
        NexusPipeline.Modules.Scripts.Validation.ProviderPlanPolicy.Validate(_script.RootPath, plan);
        bool framework = plan.ResultBasis == "framework-task-v1";
        if (!framework || _providerProjection is null)
            _providerProjection = new(plan, descriptor.PluginId, descriptor.PluginVersion, _providerRecordId,
                _activeUser?.UserId ?? "", _script.Id, attempt.Number);
        else
        {
            string[] selected = _providerProjection.RetryTaskIds();
            if (selected.Length == 0) return RunAttemptResult.Fatal("没有可确认的失败任务", "provider.retry_unavailable");
            _providerProjection.BeginAttempt(attempt.Number, selected);
        }
        var projectionGate = new object();
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var judge = new SessionJudge(framework ? new ScriptInstance() : _script);
        string attemptId = _providerRecordId + ":provider:" + attempt.Number;
        JudgeSnapshot Capture(int generation)
        {
            string scriptDir = _configRun?.ScriptDir ?? ConfigPaths.ScriptDir(_script.Id, _resolvedUser?.UserKey);
            string configurationPath = _configRun?.EffectiveConfigurationPath ?? _script.ConfigPath;
            var files = JudgeScriptRunner.CollectFiles(configurationPath, scriptDir);
            var (log, truncated) = Results.SnapshotAttemptTail(JudgeScriptRunner.MaxJudgeLogChars);
            var script = _script.Clone();
            script.ConfigPath = configurationPath;
            var user = _activeUser is null ? null : new ResolvedScriptUser(_activeUser.UserId, _activeUser.UserName, _activeUser.Binding.Clone());
            var input = System.Text.Json.Nodes.JsonNode.Parse(JudgeScriptRunner.BuildInput(script, user, files, scriptDir,
                log, truncated, _screenshotStore.MetadataForAttempt(attempt.Number)))!.AsObject();
            lock (projectionGate) input["provider"] = _providerProjection.Snapshot();
            return new(attemptId, attempt.Number, generation, log, DateTime.Now, script, user, scriptDir, input.ToJsonString(), files);
        }
        await using var workers = new RuntimeWorkers(attemptId, attempt.Number, OperationToken, "直驱", _script.Name,
            judge, status => _statusChanged?.Invoke(status), Capture, replace => _pendingReplaceConfigs = replace,
            _ => { }, (number, trigger, token) => _screenshotStore.CaptureAsync(number, trigger, token), _http);
        void ConsumeLines()
        {
            while (lines.TryDequeue(out string? line))
            {
                AppendScriptLog(line);
                if (_attemptLogLine is not null) _attemptLogLine(line, LogLevel.Info, attempt.Number);
                else _logLine?.Invoke(line, LogLevel.Info);
                judge.HandleLine(line);
            }
        }
        var port = new ProviderWorkerPort(descriptor.PluginDirectory, _script.RootPath, OriginExecutionId,
            _providerRecordId, attempt.Number, _logLine);
        // Only authenticated frames received through the Host port become evidence.
        // A managed plugin's PublishEvent callback cannot manufacture native success.
        long lastProgressPublish = 0;
        async ValueTask Publish(NexusPipeline.Plugin.Abstractions.PluginProviderEvent item)
        {
            bool changed;
            lock (projectionGate) changed = _providerProjection.Accept(attempt.Number, item);
            if (item.Kind == "log" && item.Evidence["text"]?.GetValue<string>() is { Length: > 0 } line) lines.Enqueue(line);
            if (item.Kind is not ("progress" or "log") || changed && (lastProgressPublish == 0
                || Stopwatch.GetElapsedTime(lastProgressPublish) >= TimeSpan.FromMilliseconds(250)))
            {
                lock (projectionGate) TaskReportChanged?.Invoke(_providerProjection.Snapshot());
                if (item.Kind == "progress") lastProgressPublish = Stopwatch.GetTimestamp();
            }
            await ValueTask.CompletedTask;
        }
        var mediated = new ProviderEvidenceWorkerPort(port, Publish);
        try
        {
            var context = new NexusPipeline.Plugin.Abstractions.PluginProviderRunContext(OriginExecutionId,
                _providerRecordId, attempt.Number, _activeUser?.UserId ?? "", _script.Id, plan,
                mediated, _ => ValueTask.CompletedTask, CaptureProviderLaunchTarget())
            { EffectiveConfigurationPath = framework ? "" : _configRun?.EffectiveConfigurationPath ?? "",
                SelectedTaskIds = framework ? _providerProjection.SelectedTaskIds : null };
            var execution = descriptor.Provider.RunAsync(context, OperationToken);
            while (!execution.IsCompleted && !OperationToken.IsCancellationRequested)
            {
                ConsumeLines();
                if (judge.ScriptMode && !judge.IsMarker && !judge.IsFailure
                    && (DateTime.Now - judge.LastJudgeAt).TotalSeconds >= NexusPipeline.Platform.Testing.TestHooks.ScaledSeconds(30)) workers.QueueJudge(false);
                await workers.ConsumeJudgeResultAsync().ConfigureAwait(false);
                await Task.WhenAny(execution, Task.Delay(100, OperationToken)).ConfigureAwait(false);
            }
            NexusPipeline.Plugin.Abstractions.PluginProviderRunResult result;
            try { result = await execution.ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result = new("failed", "直驱执行失败：" + ex.GetType().Name + ": " + ex.Message, port.CleanupConfirmed); }
            ConsumeLines();
            bool cancelled = OperationToken.IsCancellationRequested;
            string engine = port.TerminalReceived && port.TerminalStatus == "succeeded"
                && result.EngineStatus == "succeeded" ? "succeeded" : "failed";
            _providerProjection.Finish(engine, cancelled);
            if (judge.ScriptMode && !OperationToken.IsCancellationRequested)
            {
                workers.RequestFinalJudge();
                while (!workers.FinalJudgeCompleted)
                {
                    await workers.ConsumeJudgeResultAsync().ConfigureAwait(false);
                    workers.TryQueuePendingFinalJudge();
                    if (!workers.FinalJudgeCompleted) await Task.Delay(25, OperationToken).ConfigureAwait(false);
                }
            }
            await workers.StopAsync().ConfigureAwait(false);
            _providerProjection.ApplyJudgement(judge);
            if (!port.CleanupConfirmed || !result.WorkerCleanupConfirmed)
            {
                _pendingReplaceConfigs = null;
                _configRun?.MarkProcessCleanupUnconfirmed("provider native cleanup unconfirmed");
                return RunAttemptResult.Fatal("直驱进程清理未确认，已保留配置现场", "provider.cleanup_unconfirmed");
            }
            if (!port.WorkerStarted) return RunAttemptResult.Fatal("直驱 worker 未启动", "provider.worker_not_started");
            if (framework && engine != "succeeded" && !(port.TerminalReceived && port.TerminalStatus == "failed"
                && port.ExitCode is > 0 && result.EngineStatus == "failed"))
                return RunAttemptResult.Fatal("直驱 worker 未确认任务失败终态", "provider.terminal_unconfirmed");
            RunAttemptResult outcome = _providerProjection.Result(result.Detail);
            if (outcome.Status == "failed" && !outcome.IsFatal && !OperationToken.IsCancellationRequested
                && _pendingReplaceConfigs is { Count: > 0 })
                _configRun?.ApplyReplacements(_pendingReplaceConfigs);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            _providerProjection.Finish("cancelled", true);
            return RunAttemptResult.Cancelled("直驱执行已取消");
        }
        catch (Exception ex)
        {
            _providerProjection.Finish("failed", false);
            return RunAttemptResult.Fatal("直驱执行失败：" + ex.GetType().Name + ": " + ex.Message, "provider.failed");
        }
        finally
        {
            await workers.StopAsync().ConfigureAwait(false);
            _pendingReplaceConfigs = null;
            if (!port.CleanupConfirmed) _configRun?.MarkProcessCleanupUnconfirmed("provider worker 清理未确认");
            TaskReportChanged?.Invoke(_providerProjection.Snapshot());
        }
    }

    private NexusPipeline.Plugin.Abstractions.PluginProviderLaunchTarget? CaptureProviderLaunchTarget()
    {
        if (!_script.LaunchGame || string.IsNullOrWhiteSpace(_script.GameExe)) return null;
        if (EmulatorSupport.IsEmulator(_script)) return new("adb", _script.GameExe);
        if (_gameProcessId is not > 0) throw new InvalidDataException("provider.host_target_unavailable");
        using Process target = Process.GetProcessById(_gameProcessId.Value);
        var identity = ProcessIdentity.Capture(target);
        IntPtr window = SystemActions.FindVisibleWindow(target.Id);
        if (identity is null || !Path.IsPathFullyQualified(identity.Value.ImageName)
            || !string.Equals(Path.GetFullPath(identity.Value.ImageName), Path.GetFullPath(_script.GameExe), StringComparison.OrdinalIgnoreCase)
            || window == IntPtr.Zero) throw new InvalidDataException("provider.host_target_identity");
        return new("win32", identity.Value.ImageName, target.Id, window.ToInt64(), identity.Value.StartTime);
    }

    private sealed class ProviderEvidenceWorkerPort(ProviderWorkerPort worker,
        Func<NexusPipeline.Plugin.Abstractions.PluginProviderEvent, ValueTask> publish)
        : NexusPipeline.Plugin.Abstractions.IPluginProviderWorkerPort
    {
        public Task<NexusPipeline.Plugin.Abstractions.PluginProviderWorkerResult> RunAsync(
            NexusPipeline.Plugin.Abstractions.PluginProviderWorkerRequest request,
            Func<NexusPipeline.Plugin.Abstractions.PluginProviderEvent, ValueTask> onEvent,
            CancellationToken cancellationToken) => worker.RunAsync(request, async item =>
            {
                await publish(item).ConfigureAwait(false);
                await onEvent(item).ConfigureAwait(false);
            }, cancellationToken);
    }
}
