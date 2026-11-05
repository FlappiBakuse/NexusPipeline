using System.Globalization;
using System.Runtime.Versioning;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Secrets;
using NexusPipeline.Extensibility.Scheduling;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Secrets;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class InstalledWorkEffects(RuntimeInstance instance, StateDatabase database, Serialization.ContractReader contracts, ResourceLeaseManager resources,
    InstalledExtensionConsumption consumption, PersistentScheduleService scheduling, ExtensionSecretPort secrets,
    SecretFieldService secretStore, string extensionId, bool allowed) : IWorkEffects
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, WorkEffectBinding> _bindings = [];

    internal async ValueTask<IReadOnlyList<PreparedEffectRotation>> PrepareRotationAsync(IReadOnlyList<SecretDocumentEdit> edits, CancellationToken cancellation)
    {
        if (consumption.Work is not { } work) return [];
        WorkEffectBinding[] bindings;
        lock (_gate) bindings = _bindings.Values.Where(item => ReferenceEquals(item.Work, work) && item.IsPersisted).ToArray();
        if (bindings.Length == 0) return [];
        using var lifetime = Linked(cancellation); using var root = Reserve(work);
        return await new WorkEffectRotation(instance, database, secretStore, contracts).PrepareAsync(work, bindings, edits, lifetime.Token).ConfigureAwait(false);
    }

    internal async ValueTask FinishRotationAsync(Guid operationId, IReadOnlyList<PreparedEffectRotation> prepared, CancellationToken cancellation)
    {
        if (prepared.Count == 0) return;
        var work = Actual(); using var lifetime = Linked(cancellation); using var root = Reserve(work);
        var results = await new WorkEffectRotation(instance, database, secretStore, contracts)
            .FinishAsync(operationId, consumption.SubjectId, work, prepared, lifetime.Token).ConfigureAwait(false);
        lock (_gate)
            foreach (var id in _bindings.Keys.ToArray())
            {
                var binding = _bindings[id];
                var result = results.SingleOrDefault(value => value.Before.Key == binding.Key && value.Before.BeforeId == binding.SecretId &&
                    value.Before.BeforeRevision == binding.SecretRevision);
                if (ReferenceEquals(binding.Work, work) && result is not null)
                    _bindings[id] = binding with { SecretId = result.AfterId, SecretRevision = result.AfterRevision };
            }
        lifetime.Token.ThrowIfCancellationRequested(); work.RequireCurrent();
    }

    public async ValueTask<WorkEffectLease> BeginAsync(string field, long expectedSecretRevision, string activityId, CancellationToken cancellationToken)
    {
        Token(activityId, 80); if (expectedSecretRevision < 1) throw new ArgumentException("work_effect_secret_revision_invalid");
        using var use = await consumption.BeginAsync(cancellationToken).ConfigureAwait(false);
        var work = Actual(); using var lifetime = Linked(cancellationToken);
        var key = secrets.Authorize(work.TaskId, field, SecretAccessKind.Read);
        using var root = Reserve(work);
        var status = await secretStore.GetStateAsync(key, lifetime.Token).ConfigureAwait(false);
        if (status.Revision != expectedSecretRevision || !status.IsReadable || status.SecretId is not { } secretId)
            throw new IOException("work_effect_secret_changed");
        using var material = await secretStore.ReadAsync(key, lifetime.Token).ConfigureAwait(false);
        var version = work.RequireIdentity(); using var identity = work.ProjectIdentity(field, material);
        string family;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true))
            family = SecretReplayProof.Identity(instance, connection, key, version, identity);
        var owner = Owner(work, secretId, status.Revision);
        var effectKey = new WorkEffectKey(extensionId, field, activityId, work.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), family);
        await RequireSecretAsync(key, secretId, status.Revision, use, lifetime.Token).ConfigureAwait(false);
        _ = scheduling.ObserveEffectOwner(extensionId, owner.ProviderId, owner.TaskId, owner.OperationId, owner.RunId, owner.AttemptId, work, lifetime.Token);
        var claim = database.Write((connection, transaction) =>
        {
            use.RequireCurrent(); lifetime.Token.ThrowIfCancellationRequested();
            WorkEffectRows.Pin(connection, transaction, extensionId, field, version);
            var saved = WorkEffectRows.Find(connection, transaction, effectKey, "", "");
            if (saved is not null) return saved;
            var created = new WorkEffectClaim(Guid.NewGuid(), effectKey, null, "", "", owner);
            WorkEffectRows.Insert(connection, transaction, created); _ = WorkEffectEpochs.Open(connection, transaction, created.Id, 1, owner); return created;
        });
        var activity = ReadActivity(claim, owner, work, use, lifetime.Token);
        use.RequireCurrent(); return Bind(claim, work, key, secretId, status.Revision, activity.State, activity.Epoch, true);
    }

    public async ValueTask<WorkEffectLease> AcquireAsync(Guid activityLeaseId, string roleId, string serverId, CancellationToken cancellationToken)
    {
        Token(roleId, 128); Token(serverId, 128, empty: true);
        using var use = await consumption.BeginAsync(cancellationToken).ConfigureAwait(false);
        var work = Actual(); var parent = Require(activityLeaseId, work);
        if (parent.Claim.ParentId is not null || parent.State is WorkEffectState.Busy or WorkEffectState.Confirmed)
            throw new UnauthorizedAccessException("work_effect_parent_unavailable");
        using var lifetime = Linked(cancellationToken); using var root = Reserve(work);
        await RequireSecretAsync(parent.Key, parent.SecretId, parent.SecretRevision, use, lifetime.Token).ConfigureAwait(false);
        var found = database.Write((connection, transaction) =>
        {
            use.RequireCurrent(); lifetime.Token.ThrowIfCancellationRequested();
            var epoch = WorkEffectEpochs.Require(connection, transaction, parent.Claim.Id, parent.Epoch);
            if (epoch.Closed is not null)
                throw new UnauthorizedAccessException("work_effect_parent_completed");
            var saved = WorkEffectRows.Find(connection, transaction, parent.Claim.Key, roleId, serverId);
            if (saved is not null)
            {
                if (saved.ParentId != parent.Claim.Id) throw WorkEffectRows.Invalid();
                return (Claim: saved, Persisted: true);
            }
            var created = new WorkEffectClaim(Guid.NewGuid(), parent.Claim.Key, parent.Claim.Id, roleId, serverId,
                Owner(work, parent.SecretId, parent.SecretRevision));
            if (parent.State == WorkEffectState.Unknown) return (Claim: created, Persisted: false);
            if (epoch.Owner.OperationId != work.OperationId) throw new UnauthorizedAccessException("work_effect_epoch_unbound");
            WorkEffectRows.Insert(connection, transaction, created); return (Claim: created, Persisted: true);
        });
        var claim = found.Claim;
        var state = found.Persisted ? State(claim, work, lifetime.Token) : WorkEffectState.Unknown;
        if (parent.State == WorkEffectState.Unknown && state == WorkEffectState.New) state = WorkEffectState.Unknown;
        use.RequireCurrent(); return Bind(claim, work, parent.Key, parent.SecretId, parent.SecretRevision, state, parent.Epoch, found.Persisted);
    }

    public ValueTask SubmittedAsync(Guid leaseId, CancellationToken cancellationToken) => ChangeAsync(leaseId, "submitted", null, false, cancellationToken);
    public ValueTask ConfirmedAsync(Guid leaseId, string evidenceCode, CancellationToken cancellationToken)
    { Token(evidenceCode, 80); return ChangeAsync(leaseId, "confirmed", evidenceCode, false, cancellationToken); }
    public ValueTask CompleteAsync(Guid activityLeaseId, CancellationToken cancellationToken) =>
        ChangeAsync(activityLeaseId, "confirmed", "all_roles_confirmed", true, cancellationToken);

    private async ValueTask ChangeAsync(Guid leaseId, string kind, string? evidence, bool complete, CancellationToken cancellationToken)
    {
        using var use = await consumption.BeginAsync(cancellationToken).ConfigureAwait(false);
        var work = Actual(); var binding = Require(leaseId, work);
        if (binding.State == WorkEffectState.Busy || complete != (binding.Claim.ParentId is null) || kind == "submitted" && binding.State != WorkEffectState.New)
            throw new UnauthorizedAccessException("work_effect_transition_forbidden");
        using var lifetime = Linked(cancellationToken); using var root = Reserve(work);
        await RequireSecretAsync(binding.Key, binding.SecretId, binding.SecretRevision, use, lifetime.Token).ConfigureAwait(false);
        database.Write((connection, transaction) =>
        {
            use.RequireCurrent(); lifetime.Token.ThrowIfCancellationRequested();
            var rootId = binding.Claim.ParentId ?? binding.Claim.Id;
            var epoch = WorkEffectEpochs.Require(connection, transaction, rootId, binding.Epoch);
            var claim = binding.Claim;
            if (binding.IsPersisted)
            { if (WorkEffectRows.Require(connection, transaction, claim.Id) != claim) throw WorkEffectRows.Invalid(); }
            else
            {
                if (kind != "confirmed" || epoch.Closed is not null) throw new UnauthorizedAccessException("work_effect_confirmation_unbound");
                var existing = WorkEffectRows.Find(connection, transaction, claim.Key, claim.RoleId, claim.ServerId);
                if (existing is not null)
                {
                    if (existing.ParentId == claim.ParentId && WorkEffectRows.State(connection, transaction, existing.Id).Kind == "confirmed") return false;
                    throw new IOException("work_effect_role_changed");
                }
                WorkEffectRows.Insert(connection, transaction, claim);
            }
            var state = WorkEffectRows.State(connection, transaction, claim.Id);
            if (kind == "submitted" && (epoch.Closed is not null || epoch.Owner.OperationId != work.OperationId || state.Kind != "acquired" || !Owns(claim, work)))
                throw new UnauthorizedAccessException("work_effect_submission_forbidden");
            if (complete)
            {
                if (epoch.Closed is not null) return false;
                var children = WorkEffectRows.Children(connection, transaction, claim.Id);
                if (children.Count == 0 || children.Any(id => WorkEffectRows.State(connection, transaction, id).Kind != "confirmed"))
                    throw new IOException("work_effect_roles_unconfirmed");
                if (state.Kind != "confirmed") WorkEffectRows.Append(connection, transaction, claim.Id, Owner(work, binding.SecretId, binding.SecretRevision), kind, evidence);
                WorkEffectEpochs.Close(connection, transaction, epoch, new(work.OperationId, work.FrozenInvocation.RunId, work.FrozenInvocation.AttemptId)); return true;
            }
            if (state.Kind == "confirmed") return false;
            if (epoch.Closed is not null) throw new UnauthorizedAccessException("work_effect_parent_completed");
            WorkEffectRows.Append(connection, transaction, claim.Id, Owner(work, binding.SecretId, binding.SecretRevision), kind, evidence); return true;
        });
        use.RequireCurrent();
    }

    private WorkEffectState State(WorkEffectClaim claim, IInstalledWork work, CancellationToken cancellation)
    {
        using var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true);
        var state = WorkEffectRows.State(connection, null, claim.Id);
        var owner = claim.Owner;
        var stopped = scheduling.ObserveEffectOwner(extensionId, owner.ProviderId, owner.TaskId, owner.OperationId, owner.RunId, owner.AttemptId, work, cancellation);
        if (state.Kind == "confirmed") return WorkEffectState.Confirmed;
        if (Owns(claim, work)) return state.Kind == "acquired" ? WorkEffectState.New : WorkEffectState.Unknown;
        return stopped ? WorkEffectState.Unknown : WorkEffectState.Busy;
    }

    private (long Epoch, WorkEffectState State) ReadActivity(WorkEffectClaim claim, WorkEffectOwner owner, IInstalledWork work,
        InstalledExtensionConsumption.Use use, CancellationToken cancellation)
    {
        WorkEffectEpoch epoch;
        using (var connection = DatabaseConnections.Open(database.DatabasePath, readOnly: true)) epoch = WorkEffectEpochs.Latest(connection, null, claim.Id);
        if (epoch.Closed?.OperationId == work.OperationId) return (epoch.Ordinal, WorkEffectState.Confirmed);
        if (epoch.Closed is null && epoch.Owner.OperationId == work.OperationId) return (epoch.Ordinal, WorkEffectState.New);
        var operation = epoch.Closed?.OperationId ?? epoch.Owner.OperationId;
        var run = epoch.Closed?.RunId ?? epoch.Owner.RunId; var attempt = epoch.Closed?.AttemptId ?? epoch.Owner.AttemptId;
        var stopped = scheduling.ObserveEffectOperation(extensionId, operation, run, attempt, work, cancellation);
        if (!stopped) return (epoch.Ordinal, WorkEffectState.Busy);
        if (epoch.Closed is null) return (epoch.Ordinal, WorkEffectState.Unknown);
        var next = database.Write((connection, transaction) =>
        {
            use.RequireCurrent(); cancellation.ThrowIfCancellationRequested();
            if (WorkEffectEpochs.Latest(connection, transaction, claim.Id) != epoch) throw new IOException("work_effect_epoch_changed");
            return WorkEffectEpochs.Open(connection, transaction, claim.Id, epoch.Ordinal + 1, owner);
        });
        return (next.Ordinal, WorkEffectState.New);
    }

    private WorkEffectLease Bind(WorkEffectClaim claim, IInstalledWork work, SecretFieldKey key, Guid secretId, long revision, WorkEffectState state,
        long epoch, bool persisted)
    {
        var id = Guid.NewGuid(); var lifetime = consumption.CallbackLifetime;
        lock (_gate)
        {
            if (_bindings.Count >= 4096) throw new IOException("work_effect_lease_limit");
            _bindings.Add(id, new(claim, work, key, secretId, revision, state, epoch, persisted));
        }
        lifetime.Register(() => { lock (_gate) _bindings.Remove(id); });
        lifetime.ThrowIfCancellationRequested(); work.RequireCurrent(); return new(id, state);
    }

    private WorkEffectBinding Require(Guid id, IInstalledWork actual)
    {
        lock (_gate)
        {
            if (!_bindings.TryGetValue(id, out var binding) || !ReferenceEquals(binding.Work, actual))
                throw new UnauthorizedAccessException("work_effect_lease_unbound");
            return binding;
        }
    }

    private IInstalledWork Actual()
    {
        if (!allowed) throw new UnauthorizedAccessException("schedule_register_capability_required");
        var work = consumption.Work ?? throw new UnauthorizedAccessException("work_callback_required");
        work.RequireCurrent(); return work;
    }

    private ResourceOperationLease Reserve(IInstalledWork work) => resources.TryContinueRun(work.Activity, Guid.NewGuid(),
        [new("secrets:fields", ResourceClaimMode.Reference), new("scheduling:state", ResourceClaimMode.Mutation)]).Lease ??
        throw new IOException("work_effect_resources_unavailable");

    private async ValueTask RequireSecretAsync(SecretFieldKey key, Guid id, long revision, InstalledExtensionConsumption.Use use, CancellationToken cancellation)
    {
        use.RequireCurrent(); _ = secrets.Authorize(key.SubjectId, key.Field, SecretAccessKind.Read);
        var actual = await secretStore.GetStateAsync(key, cancellation).ConfigureAwait(false);
        if (actual.SecretId != id || actual.Revision != revision || !actual.IsReadable) throw new IOException("work_effect_secret_changed");
        cancellation.ThrowIfCancellationRequested(); use.RequireCurrent();
    }

    private CancellationTokenSource Linked(CancellationToken cancellation) => CancellationTokenSource.CreateLinkedTokenSource(cancellation, consumption.CallbackLifetime);
    private static bool Owns(WorkEffectClaim claim, IInstalledWork work) => claim.Owner.OperationId == work.OperationId &&
        claim.Owner.RunId == work.FrozenInvocation.RunId && claim.Owner.AttemptId == work.FrozenInvocation.AttemptId;
    private static WorkEffectOwner Owner(IInstalledWork work, Guid secretId, long revision) => new(work.OperationId, work.ProviderId,
        work.TaskId, work.FrozenInvocation.RunId, work.FrozenInvocation.AttemptId, secretId, revision);
    private static void Token(string value, int max, bool empty = false)
    {
        if (value is null || value.Length > max || !empty && value.Length == 0 || value.Any(c => c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.' or ':')))
            throw new ArgumentException("work_effect_identifier_invalid");
    }
}
