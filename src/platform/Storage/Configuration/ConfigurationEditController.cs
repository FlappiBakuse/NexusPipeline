using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Processes;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Automation;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Core.Operations;

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
    internal BindingDrafts Drafts { get; }
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
        Drafts = new(instance, database, contracts);
        Secrets.DraftSecretFiles.Recover(instance, database);
        _adapter = activator is null ? null : new(activator, resources, instance, contracts);
    }

    internal (ConfigurationEditAdapter? Rule, bool Exclusive) BindAdapter(string extensionId, string resourceId, string relativePath, ConfigurationDocumentAccess access, IReadOnlyList<string> arguments)
        => (_adapter ?? throw new ConfigurationException("configuration_edit_adapter_unavailable")).Bind(extensionId, resourceId, relativePath, access, arguments);

    internal FileTreeSnapshot ApplyAdapter(Guid sessionId, ConfigurationAddress address, long revision,
        FileTreeSnapshot tree, ConfigurationEditAdapter rule, string purpose) =>
        (_adapter ?? throw new ConfigurationException("configuration_edit_adapter_unavailable")).Apply(sessionId, address, revision, tree, rule, purpose);

    public ConfigurationEditStatus? Read(Guid sessionId, string subject)
    {
        _instanceLease.VerifyHeld(_instance);
        return _store.Read(sessionId, subject);
    }

    public ConfigurationEditStatus? ReadAcceptedSource(Guid sessionId, string subject, string sourceDigest, DraftBindingIdentity? draft = null)
    {
        _instanceLease.VerifyHeld(_instance);
        return _store.ReadAcceptedSource(sessionId, subject, sourceDigest, draft);
    }

    public async Task<ConfigurationEditStatus> BeginAsync(Guid sessionId, string subject, ConfigurationEditRequest request, DateTimeOffset acceptedAt)
        => await BeginAsync(sessionId, subject, request, acceptedAt, null, null);

    internal Task<ConfigurationEditStatus> BeginAsync(Guid sessionId, string subject, ConfigurationEditRequest request,
        DateTimeOffset acceptedAt, Func<ConfigurationTarget, FileTreeSnapshot, FileTreeSnapshot>? initialize) =>
        BeginAsync(sessionId, subject, request, acceptedAt, null, initialize);

    internal Task<ConfigurationEditStatus> BeginBindingAsync(Guid sessionId, string subject, ConfigurationEditRequest request,
        BindingEditorSession owner) => BeginAsync(sessionId, subject, request, DateTimeOffset.UtcNow, owner, owner.Prepare);

    private async Task<ConfigurationEditStatus> BeginAsync(Guid sessionId, string subject, ConfigurationEditRequest request,
        DateTimeOffset acceptedAt, BindingEditorSession? owner, Func<ConfigurationTarget, FileTreeSnapshot, FileTreeSnapshot>? initialize)
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
            var bindingId = request.Targets[0].Address.BindingId;
            var binding = request.DraftBinding is { } draft ? DraftBindingOwner.Resolve(catalog, bindingId, draft) :
                catalog.Bindings.SingleOrDefault(item => item.Id == bindingId) ?? throw new ConfigurationException("configuration_binding_missing");
            var claims = PhysicalResourceAdmission.Merge(state.Files.Claims
                .Concat(request.Editor.ExclusiveProgram ? PhysicalResourceAdmission.Read(request.Editor.ExecutablePath) : [])
                .Concat(request.Editor.ExclusiveProgramInUserSession ? [ExclusiveProgramAdmission.UserSessionClaim(request.Editor.ExecutablePath)] : [])
                .Concat(request.Targets.Select(target =>
                    new ResourceClaim(ConfigurationRevisionService.ResourceKey(target.Address), ResourceClaimMode.Mutation)))
                .Concat([new("automation:definitions", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference),
                    new($"configuration:script:{binding.ScriptId:D}", ResourceClaimMode.Mutation)]));
            var admission = _resources.TryAcquire(sessionId, claims);
            state.Lease = admission.Lease ?? throw new ConfigurationException(admission.Denial == ResourceLeaseDenial.Conflict
                ? "configuration_resource_conflict" : "configuration_host_unavailable");
            if (request.Editor.ExclusiveProgram)
                ExclusiveProgramAdmission.RequireStopped(request.Editor.ExecutablePath, request.Editor.ExclusiveProgramInUserSession);
            using var prepared = _store.Prepare(sessionId, request, _adapter, initialize);
            if (!prepared.AllPhysicalClaims.SequenceEqual(state.Files.Claims)) throw new IOException("configuration_file_admission_changed");
            var accepted = _store.Begin(operation, prepared, acceptedAt); acceptedHere = accepted.IsNew;
            if (!accepted.IsNew) { state.Release(); return accepted; }
            state.Subject = subject;
            state.Binding = owner; state.Request = request;
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
            if (session.IsDraft || IsBindingProfile(session)) throw new ConfigurationException("configuration_editor_binding_save_required");
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
            var session = Required(sessionId, subject);
            if (session.Phase == ConfigurationEditPhase.Cancelled && session.Revision - 1 == expectedSessionRevision &&
                CanRetryBindingResume(state, subject))
            {
                state.Binding = null; state.Request = null;
                return session;
            }
            ConfigurationEditStore.RequireOpen(session, expectedSessionRevision);
            if (state.Lease is not null) await RestoreAsync(state, session).ConfigureAwait(false);
            var cancelled = state.Lease is null ? _completion.Cancel(operation, cancel, acceptedAt) : _store.Cancel(operation, cancel, acceptedAt);
            PublishHolds(); state.Release(); Reached(ConfigurationEditBoundary.Completed, sessionId);
            return cancelled;
        }
        catch { PublishFailure(); throw; }
        finally { state.Gate.Release(); LeaveCall(); }
    }

    internal static bool IsBindingProfile(ConfigurationEditStatus value) =>
        value.Resources.All(resource => resource.Address.ResourceId.StartsWith("$editor:", StringComparison.Ordinal));

    internal ConfigurationEditStatus? ReadActiveBinding(Guid bindingId, string subject, DraftBindingIdentity? draft)
    {
        _instanceLease.VerifyHeld(_instance);
        var active = _store.ReadActiveBinding(bindingId, subject);
        if (active is null) return null;
        if (active.IsDraft != (draft is not null) || draft is not null &&
            (!_sessions.TryGetValue(active.SessionId, out var state) || state.Request?.DraftBinding != draft))
            throw new ConfigurationException("configuration_draft_binding_changed");
        return active;
    }

    internal bool CanResumeBinding(Guid sessionId) => _sessions.TryGetValue(sessionId, out var state) &&
        state.Binding is not null && state.Stopped is not null && !state.Restored;

    public async Task<BindingEditorSave> SaveBindingAsync(Guid operationId, string subject, Guid sessionId, long revision,
        string? confirmedCandidateId, DateTimeOffset acceptedAt)
    {
        ValidateFinish(operationId, subject, sessionId, revision);
        var session = Required(sessionId, subject);
        if (!session.IsDraft && !IsBindingProfile(session))
        {
            if (confirmedCandidateId is not null) throw new ConfigurationException("configuration_source_candidate_missing");
            return new(await SaveAsync(operationId, subject, sessionId, revision, acceptedAt), null, false);
        }
        var operation = new OperationIdentity(operationId, subject,
            ConfigurationEditStore.ProjectedDigest(sessionId, revision, confirmedCandidateId), "configuration.edit.save-binding");
        var state = GetSession(sessionId); await state.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            VerifyActive();
            session = Required(sessionId, subject);
            var staged = session.IsDraft && Drafts.IsStaged(operationId, subject, sessionId, operation.RequestDigest);
            if (session.IsDraft && session.Phase == ConfigurationEditPhase.Cancelled && session.CompletionOperationId == operationId)
            {
                if (!staged) throw new ConfigurationException("configuration_draft_missing");
                return new(null, null, false) { DraftCandidate = operationId };
            }
            if (!session.IsDraft && _store.LookupProjectedSave(operation, sessionId) is { } replay) return new(replay, null, false);
            ConfigurationEditStore.RequireOpen(session, revision, ConfigurationEditPhase.Editing);
            if (state.Publication is { } pending)
            {
                if (pending.Digest != operation.RequestDigest) throw new OperationConflictException();
                return PublishBinding(state, session, operation, pending, acceptedAt);
            }
            if (!IsBindingProfile(session) && session.IsDraft)
            {
                if (confirmedCandidateId is not null) throw new ConfigurationException("configuration_source_candidate_missing");
                await RestoreAsync(state, session).ConfigureAwait(false);
                var completion = _candidate.Read(session, revision, _adapter);
                _candidate.Validate(session, completion);
                state.Publication = new(operation.RequestDigest, completion, completion.Batch);
                return PublishBinding(state, session, operation, state.Publication, acceptedAt);
            }
            var owner = state.Binding ?? throw new ConfigurationException("configuration_editor_session_unavailable");
            if (state.Restored) throw new ConfigurationException("configuration_editor_session_unavailable");
            void Current()
            {
                VerifyActive();
                if (state.Lease is not { IsActive: true }) throw new IOException("configuration_edit_admission_required");
            }
            Current();
            var lease = state.Lease!;
            var first = await owner.DiscoverAsync(lease, Current).ConfigureAwait(false);
            var before = RequireCandidate(owner, first, confirmedCandidateId);
            if (before.Issue is not null)
            {
                if (state.Editor?.HasExited() == true) await StopWriterAsync(state, sessionId).ConfigureAwait(false);
                return new(null, before, state.Editor is null);
            }
            var stopped = await StopWriterAsync(state, sessionId).ConfigureAwait(false);
            var final = await owner.DiscoverAsync(lease, Current).ConfigureAwait(false);
            var after = RequireCandidate(owner, final, confirmedCandidateId);
            if (after.Issue is not null) return new(null, after, true);
            Seal(state, session, stopped);
            var captured = _candidate.Read(session, revision, _adapter);
            ConfigurationRevisionBatch publication;
            try { publication = owner.Publication(final.Candidates!.Single(), captured); }
            catch (ConfigurationException error)
            { return new(null, new(error.Message, after.Options), true); }
            await RestoreAsync(state, session).ConfigureAwait(false);
            state.Publication = new(operation.RequestDigest, captured, publication);
            return PublishBinding(state, session, operation, state.Publication, acceptedAt);
        }
        catch { PublishFailure(); throw; }
        finally { state.Gate.Release(); LeaveCall(); }
    }

    private BindingEditorSave PublishBinding(Session state, ConfigurationEditStatus session, OperationIdentity operation,
        ProjectedPublication pending, DateTimeOffset acceptedAt)
    {
        var saved = session.IsDraft ? null : _store.PublishProjected(operation, pending.Captured, pending.Publication, acceptedAt);
        if (session.IsDraft)
        {
            var owner = state.Request?.DraftBinding ?? throw new ConfigurationException("configuration_draft_missing");
            Drafts.Stage(operation.OperationId, operation.SubjectId, session.SessionId, operation.RequestDigest, owner, pending.Publication);
            var close = new ConfigurationEditCancel(session.SessionId, session.Revision);
            _store.Cancel(ConfigurationEditStore.CancelIdentity(operation.OperationId, operation.SubjectId, close), close, acceptedAt);
        }
        PublishHolds(); state.Release(); Reached(ConfigurationEditBoundary.Completed, session.SessionId);
        state.Publication = null; state.Binding = null; state.Request = null;
        return new(saved, null, false) { DraftCandidate = session.IsDraft ? operation.OperationId : null };
    }

    private sealed record ProjectedPublication(string Digest, ConfigurationEditCompletion Captured, ConfigurationRevisionBatch Publication);

    public async Task<ConfigurationEditStatus> ResumeBindingAsync(string subject, Guid sessionId, long revision, Guid nextId)
    {
        ValidateFinish(nextId, subject, sessionId, revision);
        if (nextId == sessionId) throw new ConfigurationException("configuration_edit_session_invalid");
        var digest = ConfigurationEditStore.ProjectedDigest(sessionId, revision, nextId.ToString("D"));
        var state = GetSession(sessionId); await state.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            VerifyActive();
            var replay = ReadAcceptedSource(nextId, subject, digest, state.Request?.DraftBinding);
            if (replay is not null)
            {
                if (state.ResumeId == nextId && state.ResumeFailed)
                    throw new ConfigurationException("configuration_editor_resume_unavailable");
                return replay;
            }
            var session = Required(sessionId, subject);
            var owner = state.Binding ?? throw new ConfigurationException("configuration_editor_session_unavailable");
            if (state.Stopped is null || state.Request is null)
                throw new ConfigurationException("configuration_editor_resume_unavailable");
            if (state.ResumeId is null)
            {
                ConfigurationEditStore.RequireOpen(session, revision, ConfigurationEditPhase.Editing);
                await RestoreAsync(state, session).ConfigureAwait(false);
                var cancel = new ConfigurationEditCancel(sessionId, revision);
                _store.Cancel(ConfigurationEditStore.CancelIdentity(Guid.NewGuid(), subject, cancel), cancel, DateTimeOffset.UtcNow);
                state.ResumeId = nextId; state.ResumeFailed = true;
                try { PublishHolds(); }
                finally { state.Release(); }
            }
            else if (session.Phase != ConfigurationEditPhase.Cancelled || session.Revision - 1 != revision || !CanRetryBindingResume(state, subject))
                throw new OperationConflictException();
            state.ResumeId = nextId;
            var captured = _candidate.Read(session, revision, _adapter);
            var request = state.Request with { SourceRequestDigest = digest };
            var resumed = await BeginAsync(nextId, subject, request, DateTimeOffset.UtcNow, owner.ForSession(nextId),
                (target, _) => captured.Batch.Changes.Single(change => change.Address == target.Address).Tree);
            state.ResumeFailed = false;
            return resumed;
        }
        catch { PublishFailure(); throw; }
        finally { state.Gate.Release(); LeaveCall(); }
    }

    private bool CanRetryBindingResume(Session state, string subject) => state.ResumeFailed && state.ResumeId is { } previous &&
        _store.Read(previous, subject) is null or { Phase: ConfigurationEditPhase.Cancelled };

    private static BindingEditorInspection RequireCandidate(BindingEditorSession owner,
        NexusPipeline.Extensibility.Protocol.ScriptSource source, string? confirmed)
    {
        var inspection = BindingEditorSession.Inspect(source);
        if (inspection.Issue is not null) return inspection;
        if (!owner.RunIdentityIsUnique(source.Candidates!.Single()))
            return inspection with { Issue = "configuration_editor_run_identity_ambiguous" };
        var candidate = inspection.Options.Single().Id;
        var required = confirmed ?? owner.InitialId;
        return required is not null && required != candidate ? inspection with { Issue = "configuration_editor_confirm_replacement" } : inspection;
    }

    private async Task RestoreAsync(Session state, ConfigurationEditStatus session)
    {
        if (state.Restored) return;
        VerifyActive();
        if (state.Lease is not { IsActive: true } || state.Lease.OperationId != session.SessionId)
            throw new IOException("configuration_edit_admission_required");
        var stopped = await StopWriterAsync(state, session.SessionId).ConfigureAwait(false);
        Exception? failure = null;
        try { Seal(state, session, stopped); }
        catch (Exception error) when (error is IOException or ConfigurationException) { failure = error; }
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

    private void Seal(Session state, ConfigurationEditStatus session, StoppedConfigurationEditor? stopped)
    {
        if (state.Sealed || stopped is null || session.Phase != ConfigurationEditPhase.Editing) return;
        var recorder = new EditOutputRecorder(_instance, _database, _contracts, _instanceLease);
        Exception? failure = null;
        foreach (var resource in session.Resources)
        {
            try { recorder.Seal(resource.JournalId, stopped); }
            catch (Exception error) when (error is IOException or ConfigurationException) { failure ??= error; }
        }
        if (failure is not null) throw failure;
        state.Sealed = true;
        Reached(ConfigurationEditBoundary.OutputSealed, session.SessionId);
    }

    private async Task<StoppedConfigurationEditor?> StopWriterAsync(Session state, Guid sessionId)
    {
        if (state.Editor is null) return state.Stopped;
        state.Stopped = await state.Editor.StopAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        state.Editor.Dispose(); state.Editor = null;
        Reached(ConfigurationEditBoundary.WriterStopped, sessionId);
        return state.Stopped;
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
            Drafts.Dispose();
        }
    }

    private sealed class Session
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal ResourceOperationLease? Lease;
        internal PhysicalResourceAdmission? Files;
        internal ConfigurationEditorProcess? Editor;
        internal StoppedConfigurationEditor? Stopped;
        internal BindingEditorSession? Binding;
        internal ConfigurationEditRequest? Request;
        internal Guid? ResumeId;
        internal bool ResumeFailed;
        internal ProjectedPublication? Publication;
        internal bool Restored;
        internal bool Sealed;
        internal string? Subject;
        internal void Release()
        {
            Editor?.Dispose(); Editor = null;
            Files?.Dispose(); Files = null;
            Lease?.Dispose(); Lease = null;
        }
    }
}

public sealed record BindingEditorSave(ConfigurationBatchResult? Saved, BindingEditorInspection? Inspection, bool CanResume)
{
    public Guid? DraftCandidate { get; init; }
}
