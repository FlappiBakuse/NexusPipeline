using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Operations;

namespace NexusPipeline.Platform.Storage.Configuration;

public sealed record BindingEditorSave(ConfigurationBatchResult? Saved, BindingEditorInspection? Inspection, bool CanResume);

public sealed partial class ConfigurationEditController
{
    internal static bool IsBindingProfile(ConfigurationEditStatus value) =>
        value.Resources.All(resource => resource.Address.ResourceId.StartsWith("$editor:", StringComparison.Ordinal));

    internal ConfigurationEditStatus? ReadActiveBinding(Guid bindingId, string subject)
    {
        _instanceLease.VerifyHeld(_instance);
        return _store.ReadActiveBinding(bindingId, subject);
    }

    internal bool CanResumeBinding(Guid sessionId) => _sessions.TryGetValue(sessionId, out var state) &&
        state.Binding is not null && state.Stopped is not null && !state.Restored;

    public async Task<BindingEditorSave> SaveBindingAsync(Guid operationId, string subject, Guid sessionId, long revision,
        string? confirmedCandidateId, DateTimeOffset acceptedAt)
    {
        ValidateFinish(operationId, subject, sessionId, revision);
        var session = Required(sessionId, subject);
        if (!IsBindingProfile(session))
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
            if (_store.LookupProjectedSave(operation, sessionId) is { } replay) return new(replay, null, false);
            session = Required(sessionId, subject);
            ConfigurationEditStore.RequireOpen(session, revision, ConfigurationEditPhase.Editing);
            if (state.Publication is { } pending)
            {
                if (pending.Digest != operation.RequestDigest) throw new OperationConflictException();
                return PublishBinding(state, session, operation, pending, acceptedAt);
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
        var saved = _store.PublishProjected(operation, pending.Captured, pending.Publication, acceptedAt);
        PublishHolds(); state.Release(); Reached(ConfigurationEditBoundary.Completed, session.SessionId);
        state.Publication = null; state.Binding = null; state.Request = null;
        return new(saved, null, false);
    }

    private sealed record ProjectedPublication(string Digest, ConfigurationEditCompletion Captured, ConfigurationRevisionBatch Publication);

    public async Task<ConfigurationEditStatus> ResumeBindingAsync(string subject, Guid sessionId, long revision, Guid nextId)
    {
        ValidateFinish(nextId, subject, sessionId, revision);
        if (nextId == sessionId) throw new ConfigurationException("configuration_edit_session_invalid");
        var digest = ConfigurationEditStore.ProjectedDigest(sessionId, revision, nextId.ToString("D"));
        var replay = ReadAcceptedSource(nextId, subject, digest);
        if (replay is { Phase: not ConfigurationEditPhase.Cancelled }) return replay;
        var state = GetSession(sessionId); await state.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            VerifyActive();
            replay = ReadAcceptedSource(nextId, subject, digest);
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
            else if (session.Phase != ConfigurationEditPhase.Cancelled || session.Revision - 1 != revision || !CanRetryResume(state, subject))
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

    private bool CanRetryResume(Session state, string subject) => state.ResumeFailed && state.ResumeId is { } previous &&
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
}
