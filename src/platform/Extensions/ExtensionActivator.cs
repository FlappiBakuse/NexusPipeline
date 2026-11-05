using System.Runtime.Versioning;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Extensibility.Lifecycle;
using NexusPipeline.Extensibility.Protocol;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Extensions;

public sealed class ExtensionActivator
{
    internal ProjectCompilerRegistry Compilers { get; } = new();
    internal EmulatorProviderRegistry Emulators { get; } = new();
    private readonly ContractReader _reader;
    private readonly InstanceLease? _lease;
    private readonly ResourceLeaseManager? _resources;
    private readonly ExecutionProviderRegistry? _providers;
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly ExtensionScopeShutdown.State _shutdown = new();
    private InstalledPortFactory? _ports;
    [SupportedOSPlatform("windows")]
    internal void BindPorts(InstalledPortFactory ports)
    {
        if (Interlocked.CompareExchange(ref _ports, ports, null) is { } old && !ReferenceEquals(old, ports))
            throw new IOException("extension_port_factory_changed");
    }

    public ExtensionActivator(ContractReader reader) => _reader = reader;
    internal ExtensionActivator(ContractReader reader, InstanceLease lease, ResourceLeaseManager resources,
        ExecutionProviderRegistry? providers = null)
    {
        lease.VerifyHeld(lease.Instance);
        _reader = reader; _lease = lease; _resources = resources; _providers = providers;
        lease.BindExtensionActivator(this, resources);
    }
    public ValueTask<ExtensionScope> ActivateManagedAsync(ReadOnlyMemory<byte> manifestBytes, string expectedExtensionId,
        Func<ExtensionManifest, ExtensionScope, IExtension> createExtension, CancellationToken cancellationToken) =>
        ActivateAsync(ExtensionEntryActivation.Managed(_reader, manifestBytes, expectedExtensionId, createExtension), cancellationToken);

    public ValueTask<ExtensionScope> ActivateSpecializedAsync(ReadOnlyMemory<byte> manifestBytes, string expectedExtensionId,
        Func<ExtensionManifest, ExtensionScope, CancellationToken, ValueTask> activate, CancellationToken cancellationToken) =>
        ActivateAsync(ExtensionEntryActivation.Specialized(_reader, manifestBytes, expectedExtensionId, activate), cancellationToken);

    private async ValueTask<ExtensionScope> ActivateAsync(ExtensionEntryActivation entry, CancellationToken cancellationToken)
    {
        var manifest = entry.Manifest;
        cancellationToken.ThrowIfCancellationRequested();
        var slot = BeginActivation(manifest.ExtensionId);
        var scope = slot.Scope!;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scope.Cancellation);
        try
        {
            using (scope.BeginActivationUse())
                await entry.Callback(scope, linked.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_slots.TryGetValue(manifest.ExtensionId, out var actual) || !ReferenceEquals(actual, slot) ||
                    slot.Reservation is not null || !scope.Activation.IsAccepting)
                    throw new InvalidOperationException("extension_activation_revoked");
                scope.Publish();
            }
            return scope;
        }
        catch (Exception activationFailure)
        {
            try { await scope.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Extension activation and rollback failed", activationFailure, cleanupFailure);
            }
            throw;
        }
        finally { slot.ActivationReturned.TrySetResult(); }
    }

    private Slot BeginActivation(string extensionId)
    {
        lock (_gate)
        {
            if (_shutdown.IsStopping) throw new IOException("extension_scope_inventory_stopping");
            if (_slots.TryGetValue(extensionId, out var existing) &&
                (existing.Reservation is not null || !existing.Closed || !existing.ActivationReturned.Task.IsCompletedSuccessfully ||
                    existing.Scope is not null && !existing.Scope.CleanupCompletedSuccessfully))
                throw new InvalidOperationException("extension_activation_conflict");
            existing?.Native.RequireNoResidency();
            var scope = new ExtensionScope(extensionId, Stopped);
            var slot = new Slot(scope);
            _slots[extensionId] = slot; return slot;
        }
    }

    [SupportedOSPlatform("windows")]
    internal InstalledExtensionRuntime RequirePublishedRuntime(string extensionId)
    {
        _lease?.VerifyHeld(_lease.Instance); lock (_gate)
        {
            if (_shutdown.IsStopping || !_slots.TryGetValue(extensionId, out var slot)) throw new IOException("extension_adapter_runtime_unavailable");
            var scope = slot.CandidateCommitted ? slot.CandidateScope : slot.Scope; var runtime = slot.CandidateCommitted ? slot.CandidateRuntime : slot.Runtime;
            return runtime is not null && scope is { IsPublished: true } && scope.Activation.IsAccepting ? runtime : throw new IOException("extension_adapter_runtime_unavailable");
        }
    }
    public IReadOnlyList<PublishedExtensionScope> ReadPublishedScopes()
    {
        _lease?.VerifyHeld(_lease.Instance);
        lock (_gate)
            return Array.AsReadOnly(_slots.Select(item => (item.Key, Scope: item.Value.CandidateCommitted ?
                item.Value.CandidateScope : item.Value.Scope))
                .Where(item => item.Scope is { IsPublished: true } && item.Scope.Activation.IsAccepting)
                .Select(item => new PublishedExtensionScope(item.Key, item.Scope!.ActivationId)).ToArray());
    }

    internal void RequireReplaceableNative(string extensionId)
    { lock (_gate) if (_slots.TryGetValue(extensionId, out var slot)) slot.Native.RequireNoResidency(); }

    public Task StopAllAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(120)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _lease?.VerifyHeld(_lease.Instance);
        lock (_gate)
            return _shutdown.Stop(_slots.Values.SelectMany(slot => ExtensionScopeShutdown.Members(slot.Scope,
                slot.ActivationReturned.Task, slot.CandidateScope, slot.CandidateCallback)).ToArray()).WaitAsync(timeout);
    }

    private void Stopped(ExtensionScope scope)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(scope.Activation.ExtensionId, out var slot) || scope.Activation.IsAccepting)
                throw new InvalidOperationException("extension_scope_stop_unbound");
            if (ReferenceEquals(slot.CandidateScope, scope)) slot.CandidateClosed = true;
            else if (ReferenceEquals(slot.Scope, scope)) slot.Closed = true;
            else throw new InvalidOperationException("extension_scope_stop_unbound");
        }
    }

    internal bool IsPublished(string extensionId, Guid activationId)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(extensionId, out var slot)) return false;
            var scope = slot.CandidateCommitted ? slot.CandidateScope : slot.Scope;
            return scope is not null && scope.ActivationId == activationId &&
                scope.Activation.IsPublished && scope.Activation.IsAccepting;
        }
    }

    internal async ValueTask<ApplyReservation> ReserveForApplyAsync(ResourceOperationLease root, string extensionId,
        CancellationToken acceptedCancellation, bool requireInactive = false)
    {
        if (_resources is null || _lease is null) throw new IOException("extension_scope_inventory_unavailable");
        _lease.VerifyHeld(_lease.Instance);
        _lease.RequireExtensionActivator(this, _resources);
        using var borrow = root.Borrow(_resources, root.OperationId,
            [new("extension:" + extensionId, ResourceClaimMode.Mutation)]);
        return await ApplyReservation.PrepareAsync(this, root.OperationId, extensionId, acceptedCancellation, requireInactive).ConfigureAwait(false);
    }

    internal ValueTask<ApplyReservation> ReserveForApplyAsync(Guid operationId, string extensionId, CancellationToken cancellation) =>
        ApplyReservation.PrepareAsync(this, operationId, extensionId, cancellation);

    private sealed class Slot(ExtensionScope? scope, InstalledNativeSlot? native = null)
    {
        internal readonly ExtensionScope? Scope = scope;
        internal readonly InstalledNativeSlot Native = native ?? new();
        internal readonly TaskCompletionSource ActivationReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal object? Reservation;
        internal Task? Stop;
        internal bool Closed = scope is null;
        internal ExtensionScope? CandidateScope;
        internal Task? CandidateCallback;
        internal InstalledExtensionRuntime? CandidateRuntime;
        internal bool CandidateClosed;
        internal bool CandidateCommitted;
        internal InstalledExtensionRuntime? Runtime;
    }

    internal sealed class ApplyReservation : IDisposable
    {
        private readonly ExtensionActivator _owner;
        private readonly Slot _slot;
        private readonly object _marker;
        private readonly Guid _operationId;
        private readonly string _extensionId;
        private readonly object _gate = new();
        private bool _disposed;
        private int _uses;
        private bool _released;
        private readonly CancellationToken _cancellation;
        private ApplyReservation(ExtensionActivator owner, Slot slot, object marker, Guid operationId, string extensionId, CancellationToken cancellation)
        { _cancellation = cancellation; _owner = owner; _slot = slot; _marker = marker; _operationId = operationId; _extensionId = extensionId; }

        internal static async ValueTask<ApplyReservation> PrepareAsync(ExtensionActivator owner, Guid operationId,
            string extensionId, CancellationToken cancellation, bool requireInactive = false)
        {
            cancellation.ThrowIfCancellationRequested();
            if (operationId == Guid.Empty || owner._lease is null || owner._resources is null ||
                extensionId is not { Length: >= 1 and <= 80 } || extensionId[0] is not (>= 'a' and <= 'z') ||
                extensionId[^1] == '-' || extensionId.Contains("--", StringComparison.Ordinal) ||
                extensionId.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
                throw new IOException("extension_scope_inventory_unavailable");
            owner._lease.VerifyHeld(owner._lease.Instance);
            owner._lease.RequireExtensionActivator(owner, owner._resources);
            Slot slot; var marker = new object();
            lock (owner._gate)
            {
                if (owner._shutdown.IsStopping) throw new IOException("extension_scope_inventory_stopping");
                if (!owner._slots.TryGetValue(extensionId, out slot!) ||
                    slot.Reservation is null && slot.Closed && slot.ActivationReturned.Task.IsCompletedSuccessfully &&
                    (slot.Scope is null || slot.Scope.CleanupCompletedSuccessfully))
                {
                    slot = new Slot(null, slot?.Native); slot.ActivationReturned.TrySetResult(); owner._slots[extensionId] = slot;
                }
                if (slot.Reservation is not null) throw new IOException("extension_scope_reserved");
                if (requireInactive && (slot.CandidateScope is not null || !slot.Closed ||
                    !slot.ActivationReturned.Task.IsCompletedSuccessfully ||
                    slot.Scope is not null && !slot.Scope.CleanupCompletedSuccessfully))
                    throw new IOException("extension_work_scope_referenced");
                slot.Reservation = marker; slot.Scope?.Activation.Revoke();
            }
            try
            {
                var stop = slot.Scope?.DisposeAsync().AsTask() ?? Task.CompletedTask;
                lock (owner._gate) slot.Stop = stop;
                var completed = Task.WhenAll(stop, slot.ActivationReturned.Task);
                _ = completed.ContinueWith(static fault => { _ = fault.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                await completed.WaitAsync(TimeSpan.FromSeconds(5), cancellation).ConfigureAwait(false);
                owner._lease.VerifyHeld(owner._lease.Instance);
                var result = new ApplyReservation(owner, slot, marker, operationId, extensionId, cancellation);
                result.RequireCurrent(); return result;
            }
            catch
            {
                lock (owner._gate)
                    if (ReferenceEquals(slot.Reservation, marker)) slot.Reservation = null;
                throw;
            }
        }

        internal T Use<T>(ResourceOperationLease root, Func<ExtensionObservedActivity, ExtensionScopeClosure, Guid?, T> effect)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this); _uses = checked(_uses + 1);
            }
            try
            {
                using var borrow = root.Borrow(_owner._resources!, _operationId,
                    [new("extension:" + _extensionId, ResourceClaimMode.Mutation)]);
                return borrow.Use(() =>
                {
                    RequireCurrent();
                    if (!root.IsActive) throw new IOException("extension_scope_resource_root_closed");
                    return effect(_slot.Scope is null ? ExtensionObservedActivity.None : ExtensionObservedActivity.Active,
                        _slot.Scope is null ? ExtensionScopeClosure.NotRequired : ExtensionScopeClosure.Stopped,
                        _slot.Scope?.ActivationId);
                });
            }
            finally
            {
                lock (_gate) { _uses--; ReleaseIfUnused(); }
            }
        }

        internal InstalledNativeSlot Native => _slot.Native;
        internal void RequireNativeUnmapped() { RequireCurrent(); _slot.Native.RequireNoResidency(); }

        internal ResourceOperationBorrow BorrowForActivation(ResourceOperationLease root)
        {
            RequireCurrent();
            return root.Borrow(_owner._resources!, _operationId,
                [new("extension:" + _extensionId, ResourceClaimMode.Mutation)]);
        }

        [SupportedOSPlatform("windows")]
        internal ValueTask<ExtensionApplyActivation> PrepareCandidateAsync(PreparedExtensionActivation source)
        {
            source.RequireReservation(this);
            return ExtensionApplyActivation.PrepareAsync(this, source, _owner._reader, _owner._providers, _owner._ports);
        }

        [SupportedOSPlatform("windows")]
        internal ExtensionScope BeginCandidate(PreparedExtensionActivation source, Task callback)
        {
            source.RequireReservation(this); RequireCurrent();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                lock (_owner._gate)
                {
                    if (_owner._shutdown.IsStopping || _slot.CandidateScope is not null || !ReferenceEquals(_slot.Reservation, _marker))
                        throw new IOException("extension_candidate_activation_conflict");
                    var scope = new ExtensionScope(_extensionId, _owner.Stopped);
                    _slot.CandidateScope = scope; _slot.CandidateCallback = callback;
                    return scope;
                }
            }
        }

        internal void RequireCandidate(ExtensionScope scope)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                lock (_owner._gate)
                    if (_owner._shutdown.IsStopping || !_owner._slots.TryGetValue(_extensionId, out var actual) || !ReferenceEquals(actual, _slot) ||
                        !ReferenceEquals(_slot.Reservation, _marker) || !ReferenceEquals(_slot.CandidateScope, scope))
                        throw new IOException("extension_candidate_activation_unbound");
            }
        }

        internal void AttachRuntime(ExtensionScope scope, InstalledExtensionRuntime runtime)
        {
            RequireCandidate(scope);
            lock (_owner._gate)
            {
                if (_slot.CandidateRuntime is not null || !ReferenceEquals(_slot.CandidateScope, scope))
                    throw new IOException("extension_candidate_activation_unbound");
                _slot.CandidateRuntime = runtime;
            }
        }

        internal void RequireCallbackReturned(ExtensionScope scope)
        {
            RequireCandidate(scope);
            lock (_owner._gate)
                if (_slot.CandidateCallback?.IsCompletedSuccessfully != true || _slot.CandidateRuntime is null ||
                    !scope.Activation.IsAccepting || _slot.CandidateClosed)
                    throw new IOException("extension_candidate_callback_unconfirmed");
        }

        [SupportedOSPlatform("windows")]
        internal void PublishCandidate(PreparedExtensionActivation source, ExtensionScope scope, BlobReference preparation)
        {
            source.RequireReservation(this);
            source.RequireForPublication(scope.ActivationId, preparation);
            PublishCandidateCore(source, scope);
        }

        [SupportedOSPlatform("windows")]
        internal void PublishInstalledCandidate(PreparedExtensionActivation source, ExtensionScope scope)
        {
            source.RequireReservation(this);
            source.RequireForInstalledPublication();
            PublishCandidateCore(source, scope);
        }

        [SupportedOSPlatform("windows")]
        private void PublishCandidateCore(PreparedExtensionActivation source, ExtensionScope scope)
        {
            RequireCallbackReturned(scope);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                lock (_owner._gate)
                {
                    source.RequireLifetime();
                    if (!ReferenceEquals(_slot.Reservation, _marker) || !ReferenceEquals(_slot.CandidateScope, scope) ||
                        _slot.CandidateClosed || !scope.Activation.IsAccepting) throw new IOException("extension_candidate_activation_revoked");
                    if (!scope.Activation.IsPublished) scope.Publish();
                }
            }
        }

        [SupportedOSPlatform("windows")]
        internal BoundExtensionPayload AcceptCandidateCommit(PreparedExtensionActivation source, ExtensionScope scope)
        {
            source.RequireReservation(this); RequireCallbackReturned(scope);
            var committed = source.AcceptCommitted(scope);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                lock (_owner._gate)
                {
                    source.RequireOwnerHeld();
                    if (!ReferenceEquals(_slot.Reservation, _marker) || !ReferenceEquals(_slot.CandidateScope, scope) ||
                        _slot.CandidateClosed || !scope.Activation.IsPublished || !scope.Activation.IsAccepting)
                        throw new IOException("extension_candidate_activation_revoked");
                    _slot.CandidateCommitted = true;
                }
            }
            return committed;
        }

        internal void CandidateEnded()
        {
            lock (_gate) ReleaseIfUnused();
        }

        private void RequireCurrent()
        {
            _cancellation.ThrowIfCancellationRequested();
            _owner._lease!.VerifyHeld(_owner._lease.Instance);
            lock (_owner._gate)
                if (_owner._shutdown.IsStopping || !_owner._slots.TryGetValue(_extensionId, out var actual) || !ReferenceEquals(actual, _slot) ||
                    !ReferenceEquals(_slot.Reservation, _marker) || !_slot.Closed ||
                    _slot.Stop?.IsCompletedSuccessfully != true || !_slot.ActivationReturned.Task.IsCompletedSuccessfully ||
                    _slot.Scope?.Activation.IsAccepting == true)
                    throw new IOException("extension_scope_stop_unconfirmed");
        }

        public void Dispose()
        {
            ExtensionScope? cleanup;
            lock (_gate)
            {
                _disposed = true;
                lock (_owner._gate) cleanup = _slot.CandidateCommitted ? null : _slot.CandidateScope;
                ReleaseIfUnused();
            }
            if (cleanup is not null)
            {
                var stopped = cleanup.DisposeAsync().AsTask();
                _ = stopped.ContinueWith(finished =>
                {
                    _ = finished.Exception;
                    CandidateEnded();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        private void ReleaseIfUnused()
        {
            if (!_disposed || _uses != 0 || _released) return;
            lock (_owner._gate)
            {
                if (!ReferenceEquals(_slot.Reservation, _marker)) throw new IOException("extension_scope_reservation_changed");
                if (_slot.CandidateScope is { } candidate)
                {
                    if (_slot.CandidateCommitted && candidate.Activation.IsPublished && candidate.Activation.IsAccepting)
                    {
                        var next = new Slot(candidate, _slot.Native) { Runtime = _slot.CandidateRuntime };
                        next.ActivationReturned.TrySetResult();
                        _owner._slots[_extensionId] = next;
                    }
                    else
                    {
                        if (!_slot.CandidateClosed || !candidate.CleanupCompletedSuccessfully ||
                            _slot.CandidateCallback?.IsCompleted != true) return;
                        _slot.CandidateScope = null; _slot.CandidateCallback = null; _slot.CandidateRuntime = null;
                    }
                }
                _released = true; _slot.Reservation = null;
            }
        }
    }
}
