using System.Runtime.Versioning;
using NexusPipeline.Core.Media;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class StepLiveMedia
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly AcceptedExecutionAdmission _accepted;
    private readonly StepWriterScope _scope;
    private readonly ConfigurationWriterProcess? _writer;
    private readonly ConfigurationRunController? _controller;
    private readonly PcTargetStage? _pc;
    private readonly AndroidTargetStage? _android;
    private readonly StepEvidenceBinding _binding;
    private readonly TimeSpan _stopTimeout;
    private readonly ResourceLeaseManager _resources;
    private readonly object _domainGate = new();
    private PreviewDomainSession? _domain;
    private readonly ExecutionLiveMedia _owner;
    private ExecutionLiveMedia.Step? _registration;
    private readonly SemaphoreSlim _capture = new(1, 1);
    private ILiveCaptureProcess? _process;
    private volatile bool _closing;

    internal StepLiveMedia(ExecutionLiveMedia owner, RuntimeInstance instance, StateDatabase database,
        ContractReader contracts, InstanceLease lease, AcceptedExecutionAdmission accepted, StepWriterScope scope,
        ConfigurationWriterProcess? writer, ConfigurationRunController? controller, PcTargetStage? pc,
        AndroidTargetStage? android, TimeSpan stopTimeout, ResourceLeaseManager resources)
    {
        _owner = owner; _instance = instance; _database = database; _contracts = contracts; _lease = lease; _accepted = accepted; _scope = scope;
        _writer = writer; _controller = controller; _pc = pc; _android = android; _stopTimeout = stopTimeout; _resources = resources;
        var state = ReadState();
        var job = state.Jobs.Single(job => job.StepId == scope.StepId && job.Role == "process");
        _binding = StepResultCodec.Binding(state.Latest.Root, job);
    }

    internal void PublishStarted()
    {
        if (_registration is not null || _closing) throw Closed();
        _registration = _owner.Register(_accepted.Identity.SubjectId,
            new(_binding.InstanceId, _binding.RunId, _binding.AttemptId), RequireLive, CaptureAsync,
            _pc is not null ? "pc_game" : _android is not null ? "emulator" : null);
    }

    private void RequireLive()
    {
        if (_closing || !_accepted.Root.IsActive || _accepted.Root.OperationId != _accepted.Identity.OperationId ||
            _accepted.Prepared.TimeBudget.Read().Remaining == TimeSpan.Zero) throw Closed();
        UseParent((parent, root) =>
        {
            RequireCurrentFacts();
            // The original primary remains the live authority; helpers belong to the separate read-only domain.
            return parent.UseObservedMember(Identity(root), () => true);
        });
    }

    private void RequireCurrentFacts() => _lease.PublishExecution(() =>
    {
        if (_closing || !_accepted.Root.IsActive || _accepted.Prepared.TimeBudget.Read().Remaining == TimeSpan.Zero) throw Closed();
        _accepted.Prepared.RequireLive();
        var state = _scope.VerifyReadOnly();
        if (state.IsDispatchClosed || state.Gate.State != EffectGateState.Entered || state.Journal.Phase != JournalPhase.Applying ||
            state.Journal.OperationId != _binding.ParentOperationId ||
            new StepEvidenceReader(_instance, _database, _contracts).ContextFromValidatedRead(
                _binding, state.Journal, state.Attempt, state.Total, state.Latest).Root is null) throw Closed();
    });

    private async ValueTask<PreviewImage> CaptureAsync(CancellationToken cancellation)
    {
        PreviewImage frame;
        try { frame = await CaptureDeviceAsync(cancellation).ConfigureAwait(false); }
        catch (IOException error) when (error.Message == "execution_capture_window_unavailable")
        { RequireLive(); throw new PreviewUnavailableException(PreviewAvailability.WindowNotReady); }
        catch (IOException error) when (error.Message == "run_live_capture_backend_unavailable")
        { RequireLive(); throw new PreviewUnavailableException(PreviewAvailability.Unavailable); }
        var preview = await NexusPipeline.Platform.Media.PreviewScaler.ScaleAsync(frame, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested(); RequireLive();
        return preview;
    }

    internal async ValueTask<PreviewImage> CaptureDeviceAsync(CancellationToken cancellation, string captureMethod = "printWindow")
    {
        await _capture.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            RequireLive();
            if (_process is not null) throw new IOException("media_helper_collection_pending");
            if (_pc is null && _android is null) throw new IOException("run_live_capture_backend_unavailable");
            var domain = RequireDomain();
            var id = Guid.NewGuid();
            var budget = _accepted.Prepared.TimeBudget;
            if (_pc is not null)
            {
                var runtime = ReadState().Total.CaptureRuntime ?? throw Closed();
                var window = _pc.PrepareWindow();
                try { _process = new WindowCaptureProcess(_instance, id, window, runtime, budget, domain.Job, captureMethod); }
                catch { window.Dispose(); throw; }
            }
            else if (_android is { CanRunMain: true })
            {
                var source = _android.PrepareCapture(_binding);
                _process = source.ReadFrozen().Facts.MuMu is null
                    ? new AndroidCaptureProcess(_instance, id, source, budget, domain.Job)
                    : new MuMuLiveProcess(_instance, id, source, budget, _binding,
                        ReadState().Attempt.ExecutionPlan.Sha256, domain.Job);
            }
            else throw new IOException("run_live_capture_backend_unavailable");
            try
            {
                OwnedWorkerJob? actualParent = null;
                OwnedWorkerIdentity? actualPrimary = null;
                await _process.CaptureAsync(start => UseParent((parent, primary) =>
                {
                    actualParent = parent; actualPrimary = primary;
                    return start(parent);
                }), (helper, root) =>
                {
                    // The outer callback already owns the non-reentrant configuration controller.
                    cancellation.ThrowIfCancellationRequested(); RequireCurrentFacts(); domain.RequireOpen();
                    if (actualParent is null || actualPrimary is null || actualParent.Name != _binding.JobName ||
                        root.InstanceId != _binding.InstanceId || root.AttemptId != _binding.AttemptId ||
                        actualPrimary.InstanceId != root.InstanceId || actualPrimary.AttemptId != root.AttemptId) throw Closed();
                    actualParent.UseObservedMember(Identity(actualPrimary), () =>
                        domain.Job.UseObservedMember(Identity(root), () => helper.UseObservedMember(Identity(root), () => true)));
                    cancellation.ThrowIfCancellationRequested();
                }, _stopTimeout, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested(); RequireLive();
                var image = _process.ReadLiveFrame();
                cancellation.ThrowIfCancellationRequested(); RequireLive();
                _process.RequireFramePublication();
                return image;
            }
            finally { await CollectHelperAsync().ConfigureAwait(false); }
        }
        finally { _capture.Release(); }
    }

    private async Task CollectHelperAsync()
    {
        if (_process is null) return;
        await _process.FinishAsync(_stopTimeout).ConfigureAwait(false);
        _process.Dispose(); _process = null;
    }

    internal async Task CloseAsync()
    {
        _closing = true;
        var registration = _registration?.CloseAsync(_stopTimeout + _stopTimeout) ?? Task.CompletedTask;
        PreviewDomainSession? domain;
        lock (_domainGate) domain = _domain;
        if (domain is not null) await domain.StopAsync(_stopTimeout).ConfigureAwait(false);
        await registration.ConfigureAwait(false);
        await CollectHelperAsync().ConfigureAwait(false);
        lock (_domainGate) domain = _domain;
        if (domain is not null)
        {
            await domain.StopAsync(_stopTimeout).ConfigureAwait(false);
            domain.Close(); domain.ReleaseBorrow();
            lock (_domainGate) _domain = null;
        }
    }

    private PreviewDomainSession RequireDomain()
    {
        lock (_domainGate)
        {
            if (_closing) throw Closed();
            if (_domain is null)
            {
                UseParent((_, primary) =>
                {
                    RequireCurrentFacts();
                    _domain = new(_instance, _database, _contracts, _lease, _accepted, _resources,
                        _binding, ReadState().Attempt.ExecutionPlan.Sha256, primary);
                    _domain.Open(); return true;
                });
            }
            var domain = _domain ?? throw Closed();
            domain.RequireOpen(); return domain;
        }
    }

    private T UseParent<T>(Func<OwnedWorkerJob, OwnedWorkerIdentity, T> action) => _writer is not null
        ? _writer.UsePrimaryParent(_binding, action) : (_controller ?? throw Closed()).UsePrimaryParent(_binding, action);
    private AttemptWriterState ReadState() => _lease.PublishExecution(_scope.VerifyReadOnly);
    private static ProcessIdentity Identity(OwnedWorkerIdentity root) =>
        new(root.ProcessId, root.CreatedAtUtc, root.ImagePath, root.ImageSha256, root.UserSid, root.IsElevated);
    private static IOException Closed() => new("run_live_media_step_unavailable");
}
