using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class ExecutionRuntimeStep : IDisposable
{
    private StepWriterScope? _scope;
    private ConfigurationWriterProcess? _writer;
    private ConfigurationRunController? _controller;
    private StepEvidenceCollector? _collector;
    private HookOutputCapture? _hook;
    private StepJudgementWorker? _judgementWorker;
    private ProviderControlSession? _providerControl;
    private ProviderLauncherProcess? _providerLauncher;
    private PcTargetStage? _target;
    private AndroidTargetStage? _android;
    private WindowCaptureSession? _windowCapture;
    private readonly ExecutionLiveMedia? _liveMediaOwner;
    private readonly TaskAdapterService? _adapters;
    private StepLiveMedia? _liveMedia;
    private bool _windowCaptureAttempted;
    private CancellationToken _captureCancellation;
    private readonly BindingRawAllowance _rawAllowance;
    private readonly Func<TimeSpan> _bindingElapsed;
    internal bool IsBindingBudgetExpired => _input.TotalTimeoutSeconds is > 0 && _bindingElapsed() >= TimeSpan.FromSeconds(_input.TotalTimeoutSeconds.Value);
    private readonly PreparedConfigurationRun? _preparedConfiguration;
    private readonly ResourceLeaseManager? _resources;
    private readonly ITargetPersistenceObserver? _targetObserver;
    private readonly AcceptedExecutionAdmission _accepted;
    private readonly AttemptTerminalStore _terminal;
    private readonly Guid _parentJournalId;
    private readonly ConfigurationSessionFactory _configurationFactory;
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly RecoveryIndex _index;
    private readonly ExecutionPlanStep _input;
    private readonly Guid? _configurationSession;
    private readonly TimeSpan _stopTimeout, _drainTimeout;
    private readonly Guid _completionOperation = Guid.NewGuid();
    private bool? _restoreInterrupted;
    private bool _providerInterrupted;
    private bool _activeCollected, _collected, _restored, _disposed;
    internal string StepId { get; }
    internal int InvocationOrdinal { get; private set; }
    internal bool IsCollected => _collected;
    internal bool IsRestored => _restored;
    internal IReadOnlyList<AdapterTaskObservation> TaskObservations { get; private set; } = [];
    internal bool RequiresUnregisteredConfigurationCompletion => !_restored && _configurationSession is not null &&
        (_controller is null || !_controller.HasConfigurationHistory);

    internal ExecutionRuntimeStep(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        RecoveryIndex index, AcceptedExecutionAdmission accepted, ExecutionPlanStep step, Guid? configurationSessionId, ResourceLeaseManager? resources = null, ITargetPersistenceObserver? targetObserver = null, ExecutionLiveMedia? liveMedia = null, TaskAdapterService? adapters = null, PreparedConfigurationRun? preparedConfiguration = null, BindingRawAllowance? rawAllowance = null, Func<TimeSpan>? bindingElapsed = null, ConfigurationSessionFactory? configurationFactory = null)
    {
        _configurationFactory = configurationFactory ?? new(instance, database, contracts, lease, index);
        _parentJournalId = accepted.Receipt.JournalId; StepId = step.StepId;
        _bindingElapsed = bindingElapsed ?? (() => TimeSpan.Zero);
        _preparedConfiguration = preparedConfiguration; _rawAllowance = rawAllowance ?? new(BindingOutputBudget.MaximumForStep(step), 0);
        if ((step.Target is not null || step.Provider?.Launcher is not null || step.Hooks?.Any(hook => hook.OnceAfterSuccess || hook.FinalOnly) == true) && resources is null) throw new IOException("execution_target_resource_owner_required");
        _adapters = adapters; _resources = resources; _targetObserver = targetObserver; _liveMediaOwner = liveMedia;
        _accepted = accepted; _index = index; _input = step; _configurationSession = configurationSessionId;
        _instance = instance; _database = database; _contracts = contracts; _lease = lease;
        var budget = accepted.Prepared.Total.Plan.ReadWire().Budget;
        _stopTimeout = TimeSpan.FromSeconds(Math.Min(budget.StopTimeoutSeconds, 60));
        _drainTimeout = TimeSpan.FromSeconds(Math.Min(budget.DrainTimeoutSeconds, 60));
        _terminal = new(instance, database, contracts, lease);
    }

    internal void InitializeCapture()
    {
        if (_scope is not null) throw new IOException("execution_capture_already_bound");
        var pre = _input.Hooks?.SingleOrDefault(hook => hook.Role == "preHook");
        if (pre?.OnceAfterSuccess == true)
        {
            using var proof = PreparedHookDecision.Inherit(_instance, _database, _contracts, _lease, _resources!, _accepted, StepId);
            if (proof is not null && _terminal.RecordRetryHook(_parentJournalId, proof)) { PrepareRole("process"); return; }
        }
        PrepareRole(pre is not null ? "preHook" : "process");
    }

    private void PrepareRole(string role)
    {
        if (_scope is not null && !_activeCollected) throw new IOException("execution_predecessor_uncollected");
        _accepted.Prepared.Providers.TryGetValue(StepId, out var provider);
        var scope = new StepWriterScope(_instance, _database, _contracts, _lease, _parentJournalId, StepId, role, provider: provider);
        if (_configurationSession is { } session)
        {
            if (_controller is null)
                _controller = _configurationFactory.Control(session, _accepted.Identity.SubjectId,
                    _preparedConfiguration ?? _accepted.Prepared.Configurations[StepId], scope, _accepted.Prepared.PayloadRoots);
            else _controller.AttachNextWriter(scope);
            _scope = scope; _activeCollected = false; _collected = false;
            _controller.RegisterPreparedJob();
        }
        else
        {
            var previous = _writer;
            _writer = new(_instance, _lease, scope, deferRegistration: true);
            _scope = scope; _activeCollected = false; _collected = false;
            previous?.Dispose();
            _writer.RegisterPreparedJob();
        }
        var state = _lease.PublishExecution(() => _scope!.VerifyReadOnly());
        InvocationOrdinal = ExecutionInvocationCodec.Ordinal(state.Latest.Root, StepId) + state.PreviousInvocationCount(StepId);
        var job = state.Jobs.Single(job => job.StepId == StepId && job.Role == role);
        var binding = StepResultCodec.Binding(state.Latest.Root, job);
        if (ExecutionBindingRoles.IsHook(role))
        {
            _hook = new(_instance, _database, _contracts, _lease, binding, role, _writer, _controller, _rawAllowance, state);
            return;
        }
        if (_input.Provider is not null)
            _providerControl = new(_instance, _database, _contracts, _lease, binding,
                provider ?? throw new IOException("execution_provider_activation_unbound"), _accepted.Prepared.TimeBudget, _writer, _controller, state);
        var judgement = new AttemptBusinessJudgement(binding.AttemptId, new StepEvidenceReader(_instance, _database, _contracts).Judgement(state.Total, StepId));
        if (_input.JudgementWorker is not null)
        {
            var network = _accepted.Prepared.Network ?? throw new IOException("execution_judgement_network_required");
            _judgementWorker = new(_instance, _database, _contracts, _lease, state.Total, binding, judgement,
                () => StepResultAdmission.SuppressesLocalStop(new StepEvidenceReader(_instance, _database, _contracts).AcceptedClose(binding)) ||
                    (_accepted.Prepared.TimeBudget.Read().Remaining == TimeSpan.Zero || IsBindingBudgetExpired), network.ObserveAsync, provider);
            _judgementWorker.Prepare();
        }
        Func<BlobReference, ValueTask<BlobReference?>>? observeAdapter = null;
        if (_input.Adapter is not null && _input.Purpose != "initialization")
        {
            var adapter = _adapters ?? throw new IOException("execution_adapter_service_unavailable");
            void RequireActualStep()
            {
                if (_disposed || _activeCollected || !ReferenceEquals(_scope, scope) || scope.Role != "process")
                    throw new UnauthorizedAccessException("adapter_actual_step_expired");
                _accepted.Prepared.RequireLive(); _ = _lease.PublishExecution(scope.VerifyReadOnly);
            }
            observeAdapter = async raw =>
            {
                var value = await adapter.ObserveOwnedAsync(_accepted, binding, raw, RequireActualStep).ConfigureAwait(false);
                TaskObservations = value is { } observed ? observed.Observations : [];
                return value?.Reference;
            };
        }
        _collector = _writer is not null ? new(_instance, _database, _contracts, _lease, binding, _writer, judgement, _judgementWorker, _rawAllowance, _providerControl, observeAdapter, state) :
            new(_instance, _database, _contracts, _lease, binding, _controller!, judgement, _judgementWorker, _rawAllowance, _providerControl, observeAdapter, state);
    }

    internal void EnterEffect() => (_scope ?? throw new IOException("execution_writer_unprepared")).EnterApplying();

    internal async Task<ExecutionStopReason?> ExecuteAsync(Func<ExecutionStopReason?> readStop, CancellationToken hostStopping)
    {
        try { return await ExecuteCoreAsync(readStop, hostStopping).ConfigureAwait(false); }
        catch (BudgetExpiredException)
        {
            await CollectCurrentAsync().ConfigureAwait(false);
            RecordBindingTimeout(); return null;
        }
    }

    internal void RecordBindingTimeout()
    {
        if (!_activeCollected) throw new IOException("execution_step_collection_pending");
        new BindingTimeoutStore(_instance, _database, _contracts, _lease).Record(_parentJournalId, StepId, _bindingElapsed());
        _terminal.SuppressRemainingRoles(_parentJournalId, StepId); _collected = true;
    }

    private async Task<ExecutionStopReason?> ExecuteCoreAsync(Func<ExecutionStopReason?> readStop, CancellationToken hostStopping)
    {
        _captureCancellation = hostStopping;
        if (Stop() is { } requested) return requested;
        if (_scope!.Role == "preHook")
        {
            _hook!.Start();
            var stop = await WaitForHookAsync().ConfigureAwait(false);
            if (stop is not null) return stop;
            await CollectCurrentAsync().ConfigureAwait(false);
            var state = _lease.PublishExecution(() => _scope!.VerifyReadOnly());
            var pre = HookResultCodec.Read(state.Latest.Root, state.Total).Single(hook => hook.StepId == StepId && hook.Role == "preHook");
            if (pre.Engine == EngineOutcome.Failed)
            {
                _terminal.SuppressRemainingRoles(_parentJournalId, StepId); _collected = true; return null;
            }
            if (pre.Engine != EngineOutcome.Succeeded) throw new IOException("execution_pre_hook_outcome_unknown");
            if (Stop() is { } preStop) return preStop;
            PrepareRole("process"); EnterEffect();
        }
        if (Stop() is { } beforeMain) return beforeMain;
        if (_input.Target is not null)
        {
            await using var waiting = ExecutionTargetPreview.Open(_liveMediaOwner, _instance, _accepted, _lease, _scope!,
                () => _disposed || _activeCollected || IsBindingBudgetExpired, _input.Target.Kind);
            _controller?.PrepareInstalledForTarget();
            ExecutionStopReason? targetStop;
            if (_input.Target.Kind == "pc")
            {
                _target = new(_instance, _database, _contracts, _lease, _resources!, _accepted, _input, _targetObserver, () => IsBindingBudgetExpired);
                targetStop = await _target.RunAsync(hostStopping).ConfigureAwait(false);
            }
            else if (_input.Target.Kind == "android")
            {
                _android = new(_instance, _database, _contracts, _lease, _resources!, _accepted, _input, _rawAllowance, _targetObserver, BindingRemaining);
                targetStop = await _android.RunAsync(readStop, hostStopping).ConfigureAwait(false);
            }
            else throw new IOException("execution_target_runtime_unavailable");
            if (targetStop is not null) return targetStop;
            if (IsBindingBudgetExpired) throw new BudgetExpiredException();
            var outcome = _target?.Outcome ?? _android?.Outcome;
            if (outcome is TargetExecutionOutcome.Blocked or TargetExecutionOutcome.ReadinessUnknown)
                return ExecutionStopReason.TargetUnavailable;
            if (!(_target?.CanRunMain ?? _android!.CanRunMain))
            {
                await CollectCurrentAsync().ConfigureAwait(false);
                _terminal.SuppressRemainingRoles(_parentJournalId, StepId); _collected = true; return null;
            }
        }
        if (Stop() is { } afterTarget) return afterTarget;
        if (_input.Provider?.Launcher is not null)
        {
            _controller?.PrepareInstalledForTarget();
            _providerLauncher ??= new(new ProviderLauncherStore(_instance, _database, _contracts, _lease,
                _resources!, _accepted, StepId, _targetObserver), _targetObserver);
            _providerLauncher.Register();
            if (Stop() is { } beforeLauncher) return beforeLauncher;
            _providerLauncher.Start();
            if (Stop() is { } afterLauncher) return afterLauncher;
        }
        var collector = _collector ?? throw new IOException("execution_capture_not_prepared");
        if (_liveMediaOwner is not null)
            _liveMedia = new(_liveMediaOwner, _instance, _database, _contracts, _lease, _accepted, _scope!,
                _writer, _controller, _target, _android, _stopTimeout, _resources ?? throw new IOException("media_resource_owner_required"));
        _providerControl?.BindDevice(_target, _android, _liveMedia, () => IsBindingBudgetExpired);
        if (_target is not null) _target.StartMain(() => { collector.Start(); });
        else if (_android is not null) _android.StartMain(() => { collector.Start(); });
        else collector.Start();
        _liveMedia?.PublishStarted();
        while (true)
        {
            if (Stop() is { } stop) return stop;
            if (collector.TryRequestStop()) break;
            var root = _writer is not null ? _writer.WaitForWorkAsync(TimeSpan.FromMilliseconds(200), hostStopping) :
                _controller!.WaitForWorkAsync(TimeSpan.FromMilliseconds(200), hostStopping);
            if (!collector.BusinessAccepted.IsCompleted && await Task.WhenAny(root, collector.BusinessAccepted).ConfigureAwait(false) != root) continue;
            if (await root.ConfigureAwait(false) is null) continue;
            await collector.ObserveExitAsync().ConfigureAwait(false); break;
        }
        await CollectCurrentAsync().ConfigureAwait(false);
        if (_providerInterrupted) return ExecutionStopReason.Interrupted;
        if (_input.Hooks?.Any(hook => hook.Role == "postHook") == true)
        {
            if (Stop() is { } beforePost) return beforePost;
            if (_input.Hooks.Single(hook => hook.Role == "postHook").FinalOnly && !IsBindingBudgetExpired)
            {
                using var proof = PreparedHookDecision.DeferPost(_instance, _database, _contracts, _lease, _resources!, _index, _accepted, StepId, _bindingElapsed);
                if (proof is not null && _terminal.RecordRetryHook(_parentJournalId, proof)) { _collected = true; return null; }
                if (Stop() is { } beforePostAdmission) return beforePostAdmission;
            }
            PrepareRole("postHook"); _hook!.Start();
            var stop = await WaitForHookAsync().ConfigureAwait(false);
            if (stop is not null) return stop;
            await CollectCurrentAsync().ConfigureAwait(false);
        }
        _collected = true; return null;

        ExecutionStopReason? Stop()
        {
            var reason = readStop();
            if (reason is not null and not ExecutionStopReason.TotalBudgetExhausted) return reason;
            hostStopping.ThrowIfCancellationRequested();
            if (IsBindingBudgetExpired) throw new BudgetExpiredException();
            if (reason is not null) return reason;
            return _accepted.Prepared.TimeBudget.Read().Remaining == TimeSpan.Zero ? ExecutionStopReason.TotalBudgetExhausted : null;
        }
        async Task<ExecutionStopReason?> WaitForHookAsync()
        {
            while (true)
            {
                if (Stop() is { } stop) return stop;
                if (await Wait(TimeSpan.FromMilliseconds(200), hostStopping).ConfigureAwait(false) is null) continue;
                _hook!.ObserveRootExit(); return null;
            }
        }
    }

    private TimeSpan BindingRemaining()
    {
        if (_input.TotalTimeoutSeconds is not > 0) return TimeSpan.MaxValue;
        var left = TimeSpan.FromSeconds(_input.TotalTimeoutSeconds.Value) - _bindingElapsed();
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private Task<int?> Wait(TimeSpan timeout, CancellationToken cancellation) => _writer is not null
        ? _writer.AwaitRootAsync(timeout, cancellation) : _controller!.AwaitRootAsync(timeout, cancellation);

    private async Task CollectCurrentAsync()
    {
        if (_providerControl is not null) await _providerControl.CloseDeviceAsync().ConfigureAwait(false);
        if (_liveMedia is not null) await _liveMedia.CloseAsync().ConfigureAwait(false);
        if (_activeCollected)
        {
            if (_target is not null) await _target.FinishAsync().ConfigureAwait(false);
            if (_android is not null) await _android.FinishAsync().ConfigureAwait(false);
            if (_providerLauncher is not null) await _providerLauncher.FinishAsync(_stopTimeout).ConfigureAwait(false);
            return;
        }
        if (_scope is null && _writer is null && _controller is null) { _activeCollected = true; return; }
        var state = _lease.PublishExecution(() => _scope!.VerifyReadOnly());
        var name = _writer?.JobName ?? _controller!.JobName;
        if (!state.Jobs.Any(job => job.JobName == name))
        {
            if (_collector is not null || _hook is not null || _judgementWorker is not null)
                throw new IOException("execution_unregistered_capture_unverified");
            if (_writer is not null) _writer.ReleaseEmptyJob();
            else _controller!.ReleaseEmptyJob();
            _activeCollected = true; return;
        }
        if (_scope!.Role == "process") await CaptureBeforeStopAsync(state).ConfigureAwait(false);
        var stopWatch = System.Diagnostics.Stopwatch.StartNew();
        if (_providerControl is not null && _input.Provider?.GracefulStop == true &&
            await _providerControl.RequestStopAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false))
        {
            var grace = TimeSpan.FromMilliseconds(Math.Min(10000, _stopTimeout.TotalMilliseconds / 2));
            _ = await Wait(grace, CancellationToken.None).ConfigureAwait(false);
        }
        var remainingStop = _stopTimeout - stopWatch.Elapsed;
        if (remainingStop <= TimeSpan.Zero) remainingStop = TimeSpan.FromMilliseconds(1);
        var stopped = _writer is not null ? await _writer.StopAsync(remainingStop).ConfigureAwait(false) :
            await _controller!.StopWriterAsync(remainingStop).ConfigureAwait(false);
        if (_windowCapture is not null)
        { _windowCapture.PublishAfterParent(); _windowCapture.Dispose(); _windowCapture = null; }
        state = _lease.PublishExecution(() => _scope!.VerifyReadOnly());
        if (_hook is not null)
        {
            var proof = await _hook.CollectAsync(stopped, _drainTimeout).ConfigureAwait(false);
            if (state.Gate.State == EffectGateState.Entered) _terminal.RecordHookCollected(_parentJournalId, proof);
            _hook.Dispose(); _hook = null;
        }
        else if (_collector is not null)
        {
            var proof = await _collector.CollectAsync(stopped, _drainTimeout).ConfigureAwait(false);
            _providerInterrupted = _providerControl is not null && !_providerControl.HasTerminal;
            if (state.Gate.State == EffectGateState.Entered) _terminal.RecordCollected(_parentJournalId, proof);
            _collector.Dispose(); _collector = null; _judgementWorker?.Dispose(); _judgementWorker = null;
            _providerControl?.Dispose(); _providerControl = null;
        }
        else
        {
            if (_judgementWorker is not null) await _judgementWorker.StopAsync(_stopTimeout).ConfigureAwait(false);
            var providerControl = _providerControl is null ? null : await _providerControl.FinishAsync(stopped, _drainTimeout).ConfigureAwait(false);
            if (state.Gate.State == EffectGateState.Entered)
            {
                var job = state.Jobs.Single(job => job.StepId == StepId && job.Role == _scope.Role);
                var binding = StepResultCodec.Binding(state.Latest.Root, job);
                if (ExecutionBindingRoles.IsHook(_scope.Role))
                    _terminal.RecordHookCollected(_parentJournalId, PreparedHookResult.Interrupted(
                        _instance, _database, _contracts, _lease, binding, _scope.Role));
                else _terminal.RecordCollected(_parentJournalId, new StepEvidenceFactory(_instance, _database, _contracts, _lease)
                    .CreateInterruptedCollected(binding, providerControl, _providerControl?.WorkspaceOutput));
            }
        }
        _activeCollected = true;
        if (_providerLauncher is not null) await _providerLauncher.FinishAsync(_stopTimeout).ConfigureAwait(false);
        if (_target is not null) await _target.FinishAsync().ConfigureAwait(false);
            if (_android is not null) await _android.FinishAsync().ConfigureAwait(false);
    }

    private async Task CaptureBeforeStopAsync(AttemptWriterState state)
    {
        if (!_windowCaptureAttempted)
        {
            _windowCaptureAttempted = true;
            if ((_target is not null || _android is not null) && ExecutionCaptureInputs.IsSupported(state.Total.Steps.Single(step => step.StepId == StepId)) &&
                state.Total.CaptureRuntime is { } runtime && !state.IsDispatchClosed && !_captureCancellation.IsCancellationRequested &&
                _accepted.Prepared.TimeBudget.Read().Remaining > TimeSpan.Zero)
            {
                var job = state.Jobs.Single(value => value.StepId == StepId && value.Role == "process");
                var binding = StepResultCodec.Binding(state.Latest.Root, job);
                if (new StepEvidenceReader(_instance, _database, _contracts).Context(binding).Root is not null)
                {
                    if (_target is not null)
                    {
                        PreparedPcWindow? window = null;
                        try { window = _target.PrepareWindow(); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
                        if (window is not null) _windowCapture = new(_instance, _database, _contracts, _lease, window, runtime, binding,
                            state.Attempt.ExecutionPlan.Sha256, _writer, _controller, _stopTimeout, _accepted.Prepared.TimeBudget, _targetObserver);
                    }
                    else if (_android is { CanRunMain: true })
                    {
                        PreparedCaptureContext? android = null;
                        try { android = _android.PrepareCapture(binding); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
                        if (android is not null) _windowCapture = new(_instance, _database, _contracts, _lease, android, binding,
                            state.Attempt.ExecutionPlan.Sha256, _writer, _controller, _stopTimeout, _accepted.Prepared.TimeBudget, _targetObserver);
                    }
                }
            }
        }
        if (_windowCapture is not null) await _windowCapture.FinishBeforeAsync(_captureCancellation).ConfigureAwait(false);
    }

    internal async Task CollectAsync()
    {
        if (_collected) return;
        await CollectCurrentAsync().ConfigureAwait(false); _collected = true;
    }

    internal async Task RestoreAsync(bool interrupted)
    {
        if (_restored) return;
        if (!_collected) throw new IOException("execution_step_collection_pending");
        if (RequiresUnregisteredConfigurationCompletion) return;
        _restoreInterrupted ??= interrupted;
        if (_controller is not null) await _controller.FinishAsync(_completionOperation, _restoreInterrupted.Value).ConfigureAwait(false);
        _restored = true;
    }

    internal void CompleteUnregisteredConfiguration()
    {
        if (!_collected || !RequiresUnregisteredConfigurationCompletion) throw new IOException("execution_step_completion_unbound");
        if (_controller is not null) _controller.ReleaseUnregisteredAfterCompletion();
        else
        {
            var session = new ConfigurationRunStore(_instance, _database, _contracts).Read(_configurationSession!.Value, _accepted.Identity.SubjectId)
                ?? throw new IOException("configuration_run_missing");
            if (session.Phase != ConfigurationRunPhase.Interrupted || session.CompletionOperationId is null || !session.IsWriterAdmissionClosed ||
                session.ClosedElapsedMilliseconds != 0 || session.EnteredRunning ||
                !new RunStopEvidence(_instance, _database, _contracts).HasNoJobs(session))
                throw new IOException("configuration_unregistered_completion_unverified");
        }
        _restored = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (!_collected || !_restored) throw new IOException("execution_step_completion_pending");
        _windowCapture?.Dispose(); _android?.Dispose(); _target?.Dispose(); _providerLauncher?.Dispose(); _providerControl?.Dispose(); _judgementWorker?.Dispose(); _controller?.Dispose(); _writer?.Dispose(); _disposed = true;
    }
}
