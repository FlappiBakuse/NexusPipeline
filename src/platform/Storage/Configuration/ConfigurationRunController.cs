using StepEvidenceBinding = NexusPipeline.Platform.Storage.Execution.StepEvidenceBinding;
using System.Runtime.Versioning;
using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

[SupportedOSPlatform("windows")]
public sealed class ConfigurationRunController : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _lease;
    private readonly RecoveryIndex _index;
    private readonly PayloadApplyRoots? _payloadRoots;
    private readonly PreparedConfigurationRun _prepared;
    private readonly ConfigurationRunStore _runs;
    private RunWriterProcess _writer;
    private readonly RunProgressStore _progress;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource _timerCancellation = new();
    private readonly Guid _sessionId;
    private readonly string _subject;
    private Task _monitor = Task.CompletedTask;
    private bool _startAttempted;
    private bool _installedForTarget;
    private bool _initialAttempted;
    private volatile bool _closing;
    private bool _completed;
    private bool _disposed;
    private bool _emptyJobReleased;
    private StoppedConfigurationEditor? _collectedForParent;
    private StoppedConfigurationEditor? _lastConfigurationStopped;
    private Exception? _diagnosticFailure;
    public string? SynchronizationFailureCode { get; private set; }

    public ConfigurationRunController(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        RecoveryIndex index, Guid sessionId, string subject, PreparedConfigurationRun prepared)
        : this(instance, database, contracts, lease, index, sessionId, subject, prepared, null) { }

    internal ConfigurationRunController(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        RecoveryIndex index, Guid sessionId, string subject, PreparedConfigurationRun prepared, IConfigurationWriterScope? parentScope,
        bool deferRegistration = false, PayloadApplyRoots? payloadRoots = null)
    {
        lease.VerifyHeld(instance);
        _payloadRoots = payloadRoots; _instance = instance; _database = database; _contracts = contracts; _lease = lease; _index = index;
        _sessionId = sessionId; _subject = subject; _prepared = prepared; _runs = new(instance, database, contracts); _progress = new(instance, database, contracts);
        var session = Required(); ConfigurationRunStore.RequirePrepared(session, 1);
        prepared.Use(resources =>
        {
            if (prepared.Instance != instance || prepared.OwnerOperationId != session.OwnerOperationId || resources.Count != session.Resources.Length ||
                resources.Any(item => !session.Resources.Any(resource => resource.Address == item.Address && resource.OperationId == item.Plan.OperationId &&
                    resource.JournalId == item.Plan.JournalId && resource.PlanPath == item.Reference.RelativePath && resource.PlanDigest == item.Reference.Sha256)))
                throw new IOException("configuration_run_controller_unbound");
            return true;
        });
        ConfigurationRecoveryHolds.Publish(database, index, _payloadRoots);
        _writer = new(instance, database, contracts, lease, sessionId, subject, parentScope, deferRegistration);
    }

    internal string JobName => _writer.JobName;
    internal bool HasConfigurationJobRegistration => _writer.HasConfigurationJobRegistration;
    internal bool HasConfigurationHistory => HasConfigurationJobRegistration || _lastConfigurationStopped is not null;
    internal void RegisterPreparedJob() => _writer.RegisterPreparedJob();

    internal void AttachNextWriter(IConfigurationWriterScope parentScope)
    {
        _gate.Wait();
        try
        {
            Verify();
            if (!_writer.IsHookGroup || _completed || !_closing || !_monitor.IsCompletedSuccessfully || _collectedForParent is null)
                throw new IOException("configuration_group_predecessor_uncollected");
            parentScope.RequirePreviousWriter(_writer, _collectedForParent);
            var previous = _writer;
            _writer = new(_instance, _database, _contracts, _lease, _sessionId, _subject, parentScope, deferRegistration: true);
            previous.Dispose();
            _timerCancellation.Dispose(); _timerCancellation = new();
            _monitor = Task.CompletedTask; _closing = false; _startAttempted = false; _collectedForParent = null;
        }
        finally { _gate.Release(); }
    }

    internal void ReleaseEmptyJob()
    {
        _gate.Wait();
        try
        {
            Verify();
            if (_completed || _startAttempted || HasConfigurationJobRegistration || !_monitor.IsCompletedSuccessfully || _collectedForParent is not null)
                throw new IOException("configuration_unregistered_completion_unverified");
            _writer.ReleaseEmptyJob();
            _emptyJobReleased = true; _closing = true; _timerCancellation.Cancel();
        }
        finally { _gate.Release(); }
    }

    internal void ReleaseUnregisteredAfterCompletion()
    {
        _gate.Wait();
        try
        {
            Verify();
            if (_completed || HasConfigurationJobRegistration || !_closing || !_monitor.IsCompletedSuccessfully || !_emptyJobReleased && _collectedForParent is null)
                throw new IOException("configuration_unregistered_completion_unverified");
            if (!_emptyJobReleased) _writer.RequireStoppedUnregistered(_collectedForParent!);
            var session = Required();
            if (session.Phase != ConfigurationRunPhase.Interrupted || session.CompletionOperationId is null || !session.IsWriterAdmissionClosed ||
                session.ClosedElapsedMilliseconds != 0 || session.EnteredRunning ||
                !new RunStopEvidence(_instance, _database, _contracts).HasNoJobs(session))
                throw new IOException("configuration_unregistered_completion_unverified");
            Release();
        }
        finally { _gate.Release(); }
    }

    public ConfigurationRunStatus Read() { Verify(); return Required(); }
    internal TimeSpan ElapsedSinceStart => _writer.ElapsedSinceStart;

    public Task<int?> AwaitRootAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    { Verify(); return _writer.AwaitRootAsync(timeout, cancellationToken); }
    internal Task<int?> WaitForWorkAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    { Verify(); return _writer.WaitForWorkAsync(timeout, cancellationToken); }

    internal async Task<StoppedConfigurationEditor> StopWriterAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        Task monitor;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Verify(); _closing = true; _timerCancellation.Cancel(); monitor = _monitor;
        }
        finally { _gate.Release(); }
        await monitor.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Verify();
            _collectedForParent = await _writer.StopAsync(timeout).ConfigureAwait(false);
            if (HasConfigurationJobRegistration) _lastConfigurationStopped = _collectedForParent;
            return _collectedForParent;
        }
        finally { _gate.Release(); }
    }

    internal Task AdmitProviderBootstrap(StepEvidenceBinding binding, OwnedProcessOutput output,
        Func<CancellationToken, Task> write, TimeSpan timeout)
    {
        _gate.Wait();
        try { Verify(); return _writer.AdmitProviderBootstrap(binding, output, write, timeout); }
        finally { _gate.Release(); }
    }

    internal T UsePrimaryParent<T>(StepEvidenceBinding binding, Func<OwnedWorkerJob, OwnedWorkerIdentity, T> start)
    {
        _gate.Wait();
        try
        {
            Verify();
            if (_closing || _completed || !_startAttempted) throw new IOException("execution_capture_writer_closed");
            return _writer.UsePrimaryParent(binding, start);
        }
        finally { _gate.Release(); }
    }

    internal T UseStepEvidence<T>(StepEvidenceBinding binding, StoppedConfigurationEditor? stopped,
        Func<ProcessIdentity?, int?, T> callback)
    {
        _gate.Wait();
        try
        {
            Verify();
            if (stopped is not null && (!_closing || !_monitor.IsCompletedSuccessfully || !ReferenceEquals(stopped, _collectedForParent)))
                throw new IOException("configuration_parent_collection_unverified");
            return _writer.UseStepEvidence(binding, stopped, callback);
        }
        finally { _gate.Release(); }
    }

    internal void PrepareInstalledForTarget()
    {
        _gate.Wait();
        try
        {
            Verify();
            if (_closing || _startAttempted || _completed) throw new IOException("configuration_target_install_closed");
            _writer.RequireRegisteredJob();
            _prepared.Use(_ => _lease.PublishExecution(() => EnsureInstalledCore(forTarget: true)));
        }
        finally { _gate.Release(); }
    }

    private bool EnsureInstalledCore(bool forTarget)
    {
        if (forTarget) _writer.RequireTargetInstallation();
        var session = Required();
        if (session.Phase == ConfigurationRunPhase.Prepared)
        {
            ConfigurationRunStore.RequirePrepared(session, 1);
            var exchange = new ConfigurationEditExchange(_instance, _database, _contracts);
            foreach (var resource in session.Resources) exchange.Install(resource.JournalId);
            _runs.MarkRunning(_sessionId, _subject, 1);
        }
        else if ((!_writer.IsHookGroup && !_installedForTarget) || session.Phase != ConfigurationRunPhase.Running || session.IsWriterAdmissionClosed)
            throw new IOException("configuration_group_start_closed");
        if (forTarget) _installedForTarget = true;
        return true;
    }

    public OwnedWorkerIdentity Start(OwnedProcessOutput? output = null)
    {
        _gate.Wait();
        try
        {
            Verify();
            if (_closing || _startAttempted) throw new IOException("configuration_writer_start_closed");
            _writer.RequireRegisteredJob();
            _startAttempted = true;
            var identity = _prepared.Use(_ =>
            {
                _lease.PublishExecution(() => EnsureInstalledCore(forTarget: false));
                return output is null ? _writer.Start() : _writer.Start(output);
            });
            if (_writer.Role == "process" && _prepared.Request.AutoSyncEnabled && _prepared.Request.SyncInitially) _monitor = MonitorAsync();
            return identity;
        }
        finally { _gate.Release(); }
    }

    public async Task<RunSyncResult> SynchronizeAsync(Guid operationId)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("configuration_run_progress_identity_invalid");
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Verify();
            var replay = _progress.Lookup(operationId, _subject, _sessionId);
            if (replay is not null) return replay;
            if (_closing || _completed) throw new IOException("configuration_run_sampling_closed");
            if (_initialAttempted || Required().SyncOrdinal != 0) throw new IOException("configuration_run_initial_sync_consumed");
            return _prepared.Use(_ =>
            {
                if (_writer.ElapsedSinceStart >= TimeSpan.FromSeconds(15)) _initialAttempted = true;
                using var sample = new RunProgressCapture(_instance, _database, _contracts).Read(Required(), _writer, sealedOutput: false);
                if (sample.ElapsedMilliseconds < 15000 || sample.Items.All(item => item.ReasonCode == "configuration_sync_disabled"))
                    return new RunSyncResult(operationId, sample.Session, sample.Items.Select(item =>
                        new RunSyncItem(item.Resource.Address, item.Resource.CurrentRevision, item.Resource.CurrentRevision, item.ReasonCode)).ToImmutableArray(), false);
                return _progress.Publish(operationId, _subject, sample);
            });
        }
        finally { _gate.Release(); }
    }

    public async Task<RunSyncResult> FinishAsync(Guid operationId, bool interrupted)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("configuration_run_progress_identity_invalid");
        _closing = true; _timerCancellation.Cancel();
        await _monitor.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Verify();
            var terminal = interrupted ? ConfigurationRunPhase.Interrupted : ConfigurationRunPhase.Completed;
            var replay = _progress.Lookup(operationId, _subject, _sessionId, terminal);
            if (replay is not null) { Release(); return replay; }
            if (_completed) throw new NexusPipeline.Core.Operations.OperationConflictException();
            _prepared.Use(_ => true);
            var stopped = _emptyJobReleased ? null : await _writer.StopAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            if (HasConfigurationJobRegistration) _lastConfigurationStopped = stopped;
            var outputWriter = _lastConfigurationStopped ?? stopped ?? throw new IOException("configuration_group_stop_evidence_missing");
            var session = Required();
            if (_writer.IsHookGroup && !session.IsWriterAdmissionClosed)
            {
                _lease.PublishExecution(() => new WriterAdmissionStore(_instance, _database, _contracts).CloseGroupAdmission(session));
                session = Required();
            }
            var journals = new JournalStore(_instance, _contracts);
            Exception? failure = null;
            if (session.Phase == ConfigurationRunPhase.Running)
                foreach (var resource in session.Resources)
                {
                    try
                    {
                        if (journals.Read(resource.JournalId).Phase == JournalPhase.Applied)
                            new EditOutputRecorder(_instance, _database, _contracts, _lease).Seal(resource.JournalId, outputWriter);
                    }
                    catch (Exception error) when (error is IOException or ConfigurationException) { failure ??= error; }
                }
            var exchange = new ConfigurationEditExchange(_instance, _database, _contracts);
            foreach (var resource in session.Resources)
            {
                try
                {
                    if (exchange.Restore(resource.JournalId) == ConfigurationRecoveryResult.Quarantined)
                        failure ??= new IOException("configuration_run_restoration_unverified");
                }
                catch (Exception error) when (error is IOException or ConfigurationException) { failure ??= error; }
            }
            ConfigurationRecoveryHolds.Publish(_database, _index, _payloadRoots);
            if (failure is not null) throw failure;
            if (_diagnosticFailure is not null) throw new IOException("configuration_diagnostic_persistence_failed", _diagnosticFailure);
            var result = _prepared.Use(_ =>
            {
                using var sample = _writer.IsHookGroup
                    ? new RunProgressCapture(_instance, _database, _contracts).ReadRestored(Required())
                    : new RunProgressCapture(_instance, _database, _contracts).Read(Required(), _writer, sealedOutput: true);
                return _progress.Publish(operationId, _subject, sample, terminal);
            });
            Release(); return result;
        }
        catch { ConfigurationRecoveryHolds.Publish(_database, _index, _payloadRoots); throw; }
        finally { _gate.Release(); }
    }

    private async Task MonitorAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), _timerCancellation.Token).ConfigureAwait(false);
            if (_closing) return;
            await SynchronizeAsync(Guid.NewGuid()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_timerCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            SynchronizationFailureCode = "configuration_run_synchronization_failed";
            try
            {
                var session = Required();
                var user = ConfigurationRunBinding.Read(_instance, _database, _contracts, session).Evidence.UserId;
                new Records.RunDiagnosticStore(_instance, _database).Record(session.ParentJournalId, session.StepId,
                    user is null ? null : Guid.Parse(user), "configuration-sync", SynchronizationFailureCode, error);
            }
            catch (Exception failure) when (failure is IOException or System.Text.Json.JsonException)
            { _diagnosticFailure = failure; }
        }
    }

    private ConfigurationRunStatus Required() => _runs.Read(_sessionId, _subject) ?? throw new ConfigurationException("configuration_run_missing");
    private void Verify() { ObjectDisposedException.ThrowIf(_disposed, this); _lease.VerifyHeld(_instance); }
    private void Release()
    {
        ConfigurationRecoveryHolds.Publish(_database, _index, _payloadRoots);
        _writer.Dispose(); _prepared.Dispose(); _completed = true;
    }
    public void Dispose()
    {
        if (_disposed) return;
        if (!_completed) throw new InvalidOperationException("configuration_run_pending");
        _disposed = true; _timerCancellation.Dispose();
    }
}
