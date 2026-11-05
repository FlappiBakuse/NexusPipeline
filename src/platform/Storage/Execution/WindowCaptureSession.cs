using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Configuration;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class WindowCaptureSession : IDisposable
{
    private readonly IExecutionCaptureProcess _process;
    private readonly ExecutionCaptureStore _store;
    private readonly PreparedPcWindow? _window;
    private readonly PreparedCaptureContext? _android;
    private readonly ConfigurationWriterProcess? _writer;
    private readonly ConfigurationRunController? _controller;
    private readonly ImmutableBlobStore _blobs;
    private readonly StepEvidenceBinding _primary;
    private readonly TimeSpan _stopTimeout;
    private readonly ExecutionCaptureBinding _binding;
    private readonly ITargetPersistenceObserver? _observer;
    private OwnedWorkerIdentity? _actualPrimary;
    private bool _attempted, _published, _disposed;

    internal WindowCaptureSession(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, PreparedPcWindow window, ExecutionPlanCaptureRuntime runtime,
        StepEvidenceBinding primary, string totalSha256, ConfigurationWriterProcess? writer,
        ConfigurationRunController? controller, TimeSpan stopTimeout, ExecutionTimeBudget budget,
        ITargetPersistenceObserver? observer = null)
    {
        _window = window; _primary = primary; _writer = writer; _controller = controller; _stopTimeout = stopTimeout;
        var id = Guid.NewGuid();
        WindowCaptureProcess? process = null;
        try
        {
            _blobs = new(instance, contracts);
            _process = process = new(instance, id, window, runtime, budget);
            _binding = new(primary, id, totalSha256, _process.JobName);
            _observer = observer;
            _store = new(instance, database, contracts, lease, _binding, budget, observer);
        }
        catch
        {
            try { process?.Dispose(); }
            finally { window.Dispose(); }
            throw;
        }
    }

    internal WindowCaptureSession(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, PreparedCaptureContext android, StepEvidenceBinding primary,
        string totalSha256, ConfigurationWriterProcess? writer, ConfigurationRunController? controller,
        TimeSpan stopTimeout, ExecutionTimeBudget budget, ITargetPersistenceObserver? observer = null)
    {
        _android = android; _primary = primary; _writer = writer; _controller = controller; _stopTimeout = stopTimeout;
        var id = Guid.NewGuid(); IExecutionCaptureProcess? process = null;
        try
        {
            _blobs = new(instance, contracts); _observer = observer;
            _process = process = android.ReadFrozen().Facts.MuMu is null
                ? new AndroidCaptureProcess(instance, id, android, budget)
                : new MuMuCaptureProcess(instance, id, android, budget, _blobs);
            _binding = new(primary, id, totalSha256, _process.JobName);
            _store = new(instance, database, contracts, lease, _binding, budget, observer);
            if (process is MuMuCaptureProcess mumu) mumu.Bind(_store, _binding);
        }
        catch { process?.Dispose(); throw; }
    }

    internal async Task FinishBeforeAsync(CancellationToken cancellation)
    {
        if (!_attempted)
        {
            _attempted = true;
            if (_android is not null) UseParent((_, primary) => _store.Source(_android, primary));
            else (_window ?? throw new IOException("execution_capture_source_missing"))
                .Use(facts => UseParent((_, primary) => _store.Source(facts, primary)));
            await _process.CaptureAsync(start => UseParent((parent, primary) =>
                { _actualPrimary = primary;
                    if (_process is MuMuCaptureProcess mumu) mumu.BindPrimary(primary);
                    return start(parent); }),
                (helper, root) => _store.Root(helper, root, _actualPrimary ?? throw new IOException("execution_capture_primary_missing")),
                _stopTimeout, cancellation).ConfigureAwait(false);
        }
        await _process.FinishAsync(_stopTimeout).ConfigureAwait(false);
    }

    internal void PublishAfterParent()
    {
        if (_published) return;
        if (_store.HasSource)
        {
            _observer?.Reached(TargetPersistencePoint.CaptureParentStopped, _binding);
            _process.UseStopped(_blobs, (root, inventory, png, reason, image) => _store.Collected(root, inventory, png,
                reason, _process, image, (_process as WindowCaptureProcess)?.PublishedFrames));
        }
        _published = true;
    }

    private T UseParent<T>(Func<OwnedWorkerJob, OwnedWorkerIdentity, T> start) => _writer is not null
        ? _writer.UsePrimaryParent(_primary, start)
        : (_controller ?? throw new IOException("execution_capture_primary_missing")).UsePrimaryParent(_primary, start);

    public void Dispose()
    {
        if (_disposed) return;
        if (_attempted && !_published) throw new IOException("execution_capture_collection_pending");
        _process.Dispose(); _disposed = true;
    }
}
