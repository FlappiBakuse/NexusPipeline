using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Operations;
using NexusPipeline.Platform.Networking;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Scheduling;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Windows;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
public sealed class LocalCompletionAuthorization : ICompletionLocalAuthorization, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly RuntimeInstance _instance;
    private readonly StateDatabase _database;
    private readonly InstanceLease _lease;
    private readonly CompletionIntentSource _source;
    private readonly ExecutionRunFinality _finality;
    private readonly NativeClientBinding? _native;
    private readonly TimeProvider _clock;
    private readonly Dictionary<Guid, string> _localStarts = [];
    private readonly CancellationTokenSource _ending = new();
    private CompletionAuthorizationContext? _approved;
    private long _approvedTimestamp;
    private CompletionAuthorizationContext? _authorized;
    private IReadOnlyList<Guid> _authorizedOrigins = [];
    private bool _disposed;
    private Task _nativeLifetime = Task.CompletedTask;

    public LocalCompletionAuthorization(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        ExecutionRunFinality finality, NativeClientBinding? native = null)
        : this(instance, database, contracts, lease, finality, native, TimeProvider.System) { }

    internal LocalCompletionAuthorization(RuntimeInstance instance, StateDatabase database, ContractReader contracts, InstanceLease lease,
        ExecutionRunFinality finality, NativeClientBinding? native, TimeProvider clock)
    {
        lease.VerifyHeld(instance);
        if (database.InstanceId != instance.InstanceId || database.RuntimeRoot != instance.RuntimeRoot) throw Invalid();
        _instance = instance; _database = database; _lease = lease; _source = new(instance, database, contracts, lease, finality);
        _finality = finality; _native = native; _clock = clock;
        if (_native is not null) _nativeLifetime = WatchExitAsync();
    }

    public AttemptAcceptanceResult StartLocal(ExecutionAdmissionService admission, Guid operationId, string serverSubject,
        ExecutionSelectionRequest request, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(admission);
        var identity = ExecutionStartIdentity.Create(operationId, serverSubject, request);
        lock (_gate)
        {
            RequireLive();
            if (_localStarts.TryGetValue(operationId, out var prior) && prior != identity.RequestDigest + "/" + serverSubject)
                throw new OperationConflictException();
            if (!_localStarts.ContainsKey(operationId) && _localStarts.Count >= 4096) throw new IOException("completion_local_source_capacity");
            _localStarts[operationId] = identity.RequestDigest + "/" + serverSubject;
        }
        try { return admission.Start(operationId, serverSubject, request, createdAt); }
        catch { lock (_gate) _localStarts.Remove(operationId); throw; }
    }

    public Task<bool> RequestApprovalAsync(CompletionAuthorizationContext context, CancellationToken cancellation)
    {
        lock (_gate)
        {
            if (cancellation.IsCancellationRequested) return Task.FromCanceled<bool>(cancellation);
            IReadOnlyList<Guid> origins;
            try { RequireLive(); origins = RequireSources(context); }
            catch (UnauthorizedAccessException) { return Task.FromResult(false); }
            if (IsApprovedCore(context)) return Task.FromResult(true);
            _approved = context; _approvedTimestamp = _clock.GetTimestamp();
            _authorized = context; _authorizedOrigins = origins;
            return Task.FromResult(true);
        }
    }

    public bool IsApproved(CompletionAuthorizationContext context)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            try { RequireLive(); RequireSources(context); } catch (UnauthorizedAccessException) { return false; }
            return IsApprovedCore(context);
        }
    }

    public T UseApproved<T>(CompletionAuthorizationContext context, Func<T> effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        lock (_gate)
        {
            RequireLive(); var origins = RequireSources(context);
            if (!IsApprovedCore(context)) throw new UnauthorizedAccessException("completion_local_authorization_required");
            try { return _native is null ? effect() : _native.UseVerifiedLifetime(effect); }
            finally { _approved = null; foreach (var origin in origins) _localStarts.Remove(origin); }
        }
    }

    private bool IsApprovedCore(CompletionAuthorizationContext context)
    {
        if (_approved is null || !_approved.Matches(context) || _native is not null && !_native.IsLive) return false;
        var elapsed = _clock.GetElapsedTime(_approvedTimestamp);
        return elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromMinutes(2);
    }

    public void ObserveTerminal(AttemptReceipt receipt)
    {
        lock (_gate)
        {
            if (_disposed || !_finality.IsFinal(receipt) || _source.Capture(receipt) is not null) return;
            using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
            using var transaction = connection.BeginTransaction(deferred: true);
            var current = StartSourceRows.Read(connection, transaction, receipt.OperationId);
            var original = StartSourceRows.Original(connection, transaction, current);
            _localStarts.Remove(original.Identity.OperationId);
        }
    }

    public void Forget(CompletionAuthorizationContext context)
    {
        lock (_gate)
        {
            if (_authorized is null || !_authorized.Matches(context)) return;
            _approved = null;
            foreach (var origin in _authorizedOrigins) _localStarts.Remove(origin);
            _authorizedOrigins = []; _authorized = null;
        }
    }

    public void Revoke()
    {
        lock (_gate)
        {
            _approved = null; _authorized = null; _authorizedOrigins = [];
            _localStarts.Clear();
        }
    }

    private IReadOnlyList<Guid> RequireSources(CompletionAuthorizationContext context)
    {
        if (context is null || context.Sources.Length is < 1 or > 4096) throw Invalid();
        var origins = new List<Guid>();
        using var connection = DatabaseConnections.Open(_database.DatabasePath, readOnly: true, domains: DatabaseDomain.Runtime | DatabaseDomain.Configuration | DatabaseDomain.Records | DatabaseDomain.Extensions | DatabaseDomain.System | DatabaseDomain.Content);
        using var transaction = connection.BeginTransaction(deferred: true);
        foreach (var source in context.Sources)
        {
            var receipt = AttemptReceiptStore.ReadInside(connection, transaction, source.Intent.AttemptId) ?? throw Invalid();
            var actual = _source.Capture(receipt) ?? throw Invalid();
            if (actual.Intent != source.Intent || actual.SubjectId != source.SubjectId) throw Invalid();
            var current = StartSourceRows.Read(connection, transaction, receipt.OperationId);
            var original = StartSourceRows.Original(connection, transaction, current);
            origins.Add(original.Identity.OperationId);
            if (_localStarts.TryGetValue(original.Identity.OperationId, out var known) &&
                known == original.Identity.RequestDigest + "/" + source.SubjectId) continue;
            RequireScheduled(connection, transaction, original, source);
        }
        return origins;
    }

    private void RequireScheduled(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction,
        FrozenStartSource original, CompletionAuthorizationSource source)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT occurrence_id,0 FROM main.scheduled_occurrences WHERE start_operation_id=$id " +
            "UNION ALL SELECT occurrence_id,1 FROM main.startup_queue_occurrences WHERE start_operation_id=$id;";
        command.Parameters.AddWithValue("$id", original.Identity.OperationId.ToString("D"));
        Guid occurrence; int kind;
        using (var row = command.ExecuteReader())
        {
            if (!row.Read()) throw new UnauthorizedAccessException("completion_start_origin_not_local");
            occurrence = Guid.ParseExact(row.GetString(0), "D"); kind = row.GetInt32(1);
            if (row.Read()) throw Invalid();
        }
        if (kind == 0)
        {
            var saved = ScheduleMinuteRows.ReadOccurrence(connection, transaction, occurrence, _instance.InstanceId);
            // Capture binds the accepted plan; a revalidated waiting trigger retains the minute's original revision floor.
            if (saved.SubjectId != source.SubjectId || saved.Frozen.StartOperationId != original.Identity.OperationId ||
                saved.Frozen.RunId != original.RunId || saved.Frozen.QueueId != source.Intent.QueueId ||
                saved.Frozen.QueueRevision > source.Intent.QueueRevision) throw Invalid();
        }
        else
        {
            var saved = StartupQueueRows.Read(connection, transaction, occurrence, _instance.InstanceId);
            if (source.SubjectId != SavedStartupQueue.Subject || saved.Occurrence.StartOperationId != original.Identity.OperationId ||
                saved.Occurrence.RunId != original.RunId || saved.Occurrence.QueueId != source.Intent.QueueId ||
                saved.Occurrence.QueueRevision > source.Intent.QueueRevision) throw Invalid();
        }
    }

    private async Task WatchExitAsync()
    {
        try { await _native!.WaitForExitAsync(_ending.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_ending.IsCancellationRequested) { return; }
        catch (IOException) { }
        finally { _ending.Cancel(); }
    }

    private void RequireLive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lease.VerifyHeld(_instance);
        if (_ending.IsCancellationRequested) throw new UnauthorizedAccessException("completion_local_authorization_ended");
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _approved = null; _authorized = null; _authorizedOrigins = [];
            _ending.Cancel(); _localStarts.Clear();
        }
        try { await _nativeLifetime.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_ending.IsCancellationRequested) { }
        finally { _ending.Dispose(); }
    }

    private static IOException Invalid() => new("completion_local_authority_unbound");
}
