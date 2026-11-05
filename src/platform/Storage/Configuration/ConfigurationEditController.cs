using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Configuration;

public enum ConfigurationEditBoundary { Accepted, Installed, Editing, WriterStarted, WriterStopped, OutputSealed, Restored, Completed }
public interface IConfigurationEditObserver { void Reached(ConfigurationEditBoundary boundary, Guid sessionId); }

[SupportedOSPlatform("windows")]
public sealed class ConfigurationEditController : IDisposable
{
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly ContractReader _contracts;
    private readonly InstanceLease _instanceLease;
    private readonly ResourceLeaseManager _resources;
    private readonly RecoveryIndex _index;
    private readonly ConfigurationEditStore _store;
    private readonly ConfigurationEditExchange _exchange;
    private readonly ConfigurationEditCandidate _candidate;
    private readonly EditCompletionService _completion;
    private readonly AdapterEditHook? _adapter;
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly IConfigurationEditObserver? _observer;
    private readonly object _admissionGate = new();
    private bool _disposed;
    private bool _stopping;
    private int _activeCalls;

    public ConfigurationEditController(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease instanceLease, ResourceLeaseManager resources, RecoveryIndex index, IConfigurationEditObserver? observer = null, NexusPipeline.Platform.Extensions.ExtensionActivator? activator = null)
    {
        instanceLease.VerifyHeld(instance);
        _instance = instance; _database = database; _contracts = contracts; _instanceLease = instanceLease;
        _resources = resources; _index = index; _observer = observer;
        _store = new(instance, database, contracts); _exchange = new(instance, database, contracts); _candidate = new(instance, database, contracts);
        _completion = new(instance, database, contracts, instanceLease, resources, index);
        _adapter = activator is null ? null : new(activator, resources, instance, contracts);
    }

    internal (ConfigurationEditAdapter? Rule, bool Exclusive) BindAdapter(string extensionId, string resourceId, string relativePath, ConfigurationDocumentAccess access, IReadOnlyList<string> arguments)
        => (_adapter ?? throw new ConfigurationException("configuration_edit_adapter_unavailable")).Bind(extensionId, resourceId, relativePath, access, arguments);

    public ConfigurationEditStatus? Read(Guid sessionId, string subject)
    {
        _instanceLease.VerifyHeld(_instance);
        return _store.Read(sessionId, subject);
    }

    public ConfigurationEditStatus? ReadAcceptedSource(Guid sessionId, string subject, string sourceDigest)
    {
        _instanceLease.VerifyHeld(_instance);
        return _store.ReadAcceptedSource(sessionId, subject, sourceDigest);
    }

    public async Task<ConfigurationEditStatus> BeginAsync(Guid sessionId, string subject, ConfigurationEditRequest request, DateTimeOffset acceptedAt)
    {
        request = ConfigurationEditPolicy.Freeze(request);
        if (request.Editor is null || request.Targets.Any(target => target.WritePolicy is null))
            throw new ConfigurationException("configuration_editor_declaration_required");
        var operation = ConfigurationEditStore.BeginIdentity(sessionId, subject, request);
        var state = GetSession(sessionId); await state.Gate.WaitAsync().ConfigureAwait(false);
        var acceptedHere = false; var createdResources = false;
        try
        {
            VerifyActive();
            lock (_admissionGate) if (_stopping) throw new IOException("configuration_editor_stopping");
            var replay = _store.LookupBegin(operation, request);
            if (replay is not null) return replay;
            createdResources = true;
            ConfigurationEditPreparation.ValidateTargets(_instance, request);
            using var image = HoldImage(request.Editor);
            using var cwd = StableDirectoryLease.Acquire(request.Editor.WorkingDirectory);
            state.Files = PhysicalResourceAdmission.Prepare(request.Targets.Select(target => target.TargetPath));
            var catalog = new AutomationDefinitionStore(_database).Read();
            var binding = catalog.Bindings.SingleOrDefault(item => item.Id == request.Targets[0].Address.BindingId)
                ?? throw new ConfigurationException("configuration_binding_missing");
            var claims = PhysicalResourceAdmission.Merge(state.Files.Claims
                .Concat(request.Editor.ExclusiveProgram ? PhysicalResourceAdmission.Read(request.Editor.ExecutablePath) : [])
                .Concat(request.Targets.Select(target =>
                    new ResourceClaim(ConfigurationRevisionService.ResourceKey(target.Address), ResourceClaimMode.Mutation)))
                .Concat([new("automation:definitions", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference),
                    new($"configuration:script:{binding.ScriptId:D}", ResourceClaimMode.Mutation)]));
            var admission = _resources.TryAcquire(sessionId, claims);
            state.Lease = admission.Lease ?? throw new ConfigurationException(admission.Denial == ResourceLeaseDenial.Conflict
                ? "configuration_resource_conflict" : "configuration_host_unavailable");
            using var prepared = _store.Prepare(sessionId, request, _adapter);
            if (!prepared.AllPhysicalClaims.SequenceEqual(state.Files.Claims)) throw new IOException("configuration_file_admission_changed");
            var accepted = _store.Begin(operation, prepared, acceptedAt); acceptedHere = accepted.IsNew;
            if (!accepted.IsNew) { state.Release(); return accepted; }
            state.Subject = subject;
            PublishHolds(); Reached(ConfigurationEditBoundary.Accepted, sessionId);
            state.Editor = new(_instance, _database, _contracts, _instanceLease, sessionId, subject);
            foreach (var resource in accepted.Resources) _exchange.Install(resource.JournalId);
            Reached(ConfigurationEditBoundary.Installed, sessionId);
            var editing = _store.MarkEditing(sessionId, subject, 1); Reached(ConfigurationEditBoundary.Editing, sessionId);
            lock (_admissionGate) if (_stopping) throw new IOException("configuration_editor_stopping");
            state.Editor.Start(); Reached(ConfigurationEditBoundary.WriterStarted, sessionId);
            return editing;
        }
        catch
        {
            if (acceptedHere)
            {
                try
                {
                    var current = Required(sessionId, subject);
                    await RestoreAsync(state, current).ConfigureAwait(false);
                    var cancel = new ConfigurationEditCancel(sessionId, current.Revision);
                    _store.Cancel(ConfigurationEditStore.CancelIdentity(Guid.NewGuid(), subject, cancel), cancel, DateTimeOffset.UtcNow);
                    PublishHolds(); state.Release();
                }
                catch { PublishFailure(); }
            }
            else if (createdResources) state.Release();
            throw;
        }
        finally { state.Gate.Release(); LeaveCall(); }
    }

    public async Task<ConfigurationBatchResult> SaveAsync(Guid operationId, string subject, Guid sessionId, long expectedSessionRevision, DateTimeOffset acceptedAt)
    {
        ValidateFinish(operationId, subject, sessionId, expectedSessionRevision);
        var state = GetSession(sessionId); await state.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            VerifyActive();
            var session = Required(sessionId, subject);
            if (session.Phase is ConfigurationEditPhase.Prepared or ConfigurationEditPhase.Editing)
            {
                ConfigurationEditStore.RequireOpen(session, expectedSessionRevision, ConfigurationEditPhase.Editing);
                if (state.Lease is not null) await RestoreAsync(state, session).ConfigureAwait(false);
            }
            var completion = _candidate.Read(session, expectedSessionRevision, _adapter);
            var operation = ConfigurationEditStore.SaveIdentity(operationId, subject, completion);
            var replay = _store.LookupSave(operation, completion);
            if (replay is not null) { PublishHolds(); state.Release(); return replay; }
            _candidate.Validate(session, completion);
            var saved = state.Lease is null ? _completion.Save(operation, completion, acceptedAt) : _store.Save(operation, completion, acceptedAt);
            PublishHolds(); state.Release(); Reached(ConfigurationEditBoundary.Completed, sessionId);
            return saved;
        }
        catch { PublishFailure(); throw; }
        finally { state.Gate.Release(); LeaveCall(); }
    }

    public async Task<ConfigurationEditStatus> CancelAsync(Guid operationId, string subject, Guid sessionId, long expectedSessionRevision, DateTimeOffset acceptedAt)
    {
        ValidateFinish(operationId, subject, sessionId, expectedSessionRevision);
        var state = GetSession(sessionId); await state.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            VerifyActive();
            var cancel = new ConfigurationEditCancel(sessionId, expectedSessionRevision);
            var operation = ConfigurationEditStore.CancelIdentity(operationId, subject, cancel);
            var replay = _store.LookupCancel(operation, cancel);
            if (replay is not null) { PublishHolds(); state.Release(); return replay; }
            var session = Required(sessionId, subject); ConfigurationEditStore.RequireOpen(session, expectedSessionRevision);
            if (state.Lease is not null) await RestoreAsync(state, session).ConfigureAwait(false);
            var cancelled = state.Lease is null ? _completion.Cancel(operation, cancel, acceptedAt) : _store.Cancel(operation, cancel, acceptedAt);
            PublishHolds(); state.Release(); Reached(ConfigurationEditBoundary.Completed, sessionId);
            return cancelled;
        }
        catch { PublishFailure(); throw; }
        finally { state.Gate.Release(); LeaveCall(); }
    }

    private async Task RestoreAsync(Session state, ConfigurationEditStatus session)
    {
        if (state.Restored) return;
        VerifyActive();
        if (state.Lease is not { IsActive: true } || state.Lease.OperationId != session.SessionId)
            throw new IOException("configuration_edit_admission_required");
        StoppedConfigurationEditor? stopped = null;
        if (state.Editor is not null)
        {
            stopped = await state.Editor.StopAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            state.Editor.Dispose(); Reached(ConfigurationEditBoundary.WriterStopped, session.SessionId);
        }
        Exception? failure = null;
        if (stopped is not null && session.Phase == ConfigurationEditPhase.Editing)
        {
            var recorder = new EditOutputRecorder(_instance, _database, _contracts, _instanceLease);
            foreach (var resource in session.Resources)
            {
                try { recorder.Seal(resource.JournalId, stopped); }
                catch (Exception error) when (error is IOException or ConfigurationException) { failure ??= error; }
            }
            if (failure is null) Reached(ConfigurationEditBoundary.OutputSealed, session.SessionId);
        }
        foreach (var resource in session.Resources)
        {
            try
            {
                if (_exchange.Restore(resource.JournalId) == ConfigurationRecoveryResult.Quarantined)
                    failure ??= new IOException("configuration_edit_restoration_unverified");
            }
            catch (IOException error) { failure ??= error; }
        }
        PublishHolds();
        if (failure is not null) { PublishFailure(); throw failure; }
        state.Restored = true; Reached(ConfigurationEditBoundary.Restored, session.SessionId);
    }

    private Session GetSession(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new ConfigurationException("configuration_edit_session_invalid");
        lock (_admissionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions.TryGetValue(sessionId, out var state)) { _activeCalls = checked(_activeCalls + 1); return state; }
            if (_sessions.Count >= 4096) throw new ConfigurationException("configuration_edit_controller_limit");
            state = new(); _sessions[sessionId] = state; _activeCalls = checked(_activeCalls + 1); return state;
        }
    }

    private void LeaveCall() { lock (_admissionGate) _activeCalls--; }

    private FileStream HoldImage(ConfigurationEditorCommand command)
    {
        var path = PhysicalPathPolicy.NormalizeWithoutLinks(command.ExecutablePath);
        if (path != command.ExecutablePath || PhysicalPathPolicy.NormalizeWithoutLinks(command.WorkingDirectory) != command.WorkingDirectory)
            throw new ConfigurationException("configuration_editor_command_invalid");
        var image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexStringLower(SHA256.HashData(image)) == command.ExecutableSha256) return image;
        image.Dispose(); throw new IOException("configuration_editor_image_changed");
    }

    private ConfigurationEditStatus Required(Guid id, string subject) => _store.Read(id, subject) ?? throw new ConfigurationException("configuration_edit_missing");
    private static void ValidateFinish(Guid operationId, string subject, Guid sessionId, long revision)
    {
        ConfigurationEditPolicy.ValidateSession(sessionId, revision);
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(subject) || subject.Length > 256)
            throw new ArgumentException("configuration_edit_completion_identity_invalid");
    }
    private void VerifyActive() { ObjectDisposedException.ThrowIf(_disposed, this); _instanceLease.VerifyHeld(_instance); }
    private void Reached(ConfigurationEditBoundary boundary, Guid id) => _observer?.Reached(boundary, id);
    private void PublishHolds() => ConfigurationEditRecovery.PublishHolds(_database, _index);
    private void PublishFailure()
    {
        try
        {
            var evidence = new JournalRecoveryEvidence(_instance, new(_instance, _contracts), _database);
            new RecoveryCoordinator(new RecoveryJournalRegistry(_database), evidence, _index).VerifyAndPublish();
            PublishHolds();
        }
        catch { _index.BlockUnknown("configuration_edit_reconciliation_required"); }
    }

    public void PreventNewBegins()
    {
        lock (_admissionGate) _stopping = true;
    }

    public async Task StopAsync(TimeSpan gateWaitTimeout)
    {
        if (gateWaitTimeout <= TimeSpan.Zero || gateWaitTimeout > TimeSpan.FromSeconds(120))
            throw new ArgumentOutOfRangeException(nameof(gateWaitTimeout));
        KeyValuePair<Guid, Session>[] sessions;
        lock (_admissionGate)
        {
            if (_disposed) return;
            _stopping = true;
            sessions = _sessions.ToArray();
        }
        using var wait = new CancellationTokenSource(gateWaitTimeout);
        foreach (var (id, state) in sessions)
        {
            await state.Gate.WaitAsync(wait.Token).ConfigureAwait(false);
            try
            {
                if (state.Lease is null) continue;
                var subject = state.Subject ?? throw new IOException("configuration_editor_session_unbound");
                var current = Required(id, subject);
                await RestoreAsync(state, current).ConfigureAwait(false);
                var cancel = new ConfigurationEditCancel(id, current.Revision);
                _store.Cancel(ConfigurationEditStore.CancelIdentity(Guid.NewGuid(), subject, cancel), cancel, DateTimeOffset.UtcNow);
                PublishHolds();
                state.Release();
            }
            catch { PublishFailure(); throw; }
            finally { state.Gate.Release(); }
        }
    }

    public void Dispose()
    {
        lock (_admissionGate)
        {
            if (_disposed) return;
            if (_activeCalls != 0 || _sessions.Values.Any(state => state.Lease is not null)) throw new InvalidOperationException("configuration_edits_pending");
            _disposed = true;
        }
    }

    private sealed class Session
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal ResourceOperationLease? Lease;
        internal PhysicalResourceAdmission? Files;
        internal ConfigurationEditorProcess? Editor;
        internal bool Restored;
        internal string? Subject;
        internal void Release()
        {
            Editor?.Dispose(); Editor = null;
            Files?.Dispose(); Files = null;
            Lease?.Dispose(); Lease = null;
        }
    }
}
