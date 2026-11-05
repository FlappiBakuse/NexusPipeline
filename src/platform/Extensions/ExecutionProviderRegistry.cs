using System.Runtime.Versioning;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Execution;
using NexusPipeline.Platform.Storage.Extensions;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;
using NexusPipeline.Platform.Serialization;

namespace NexusPipeline.Platform.Extensions;

public sealed class ExecutionProviderRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);

    private readonly Dictionary<Guid, RetainedEntry> _retained = [];
    private readonly CancellationTokenSource _monitorStopping = new();
    private Task _monitor = Task.CompletedTask;
    private bool _monitorRunning;
    internal int RetainedCount { get { lock (_gate) return _retained.Count; } }

    private void ReserveRetained(Guid id)
    {
        lock (_gate)
        {
            if (_retained.Count >= JournalCapacityLimits.MaximumOwnedJobs || !_retained.TryAdd(id, new(null, null, false)))
                throw new IOException("execution_provider_retained_capacity");
        }
    }

    private void ReleaseReservation(Guid id, bool onlyUnclaimed = false)
    {
        lock (_gate) if (_retained.TryGetValue(id, out var value) && value.Live is null && value.Recovered is null && (!onlyUnclaimed || !value.Claimed)) _retained.Remove(id);
    }

    [SupportedOSPlatform("windows")]
    private void KeepRetained(ProviderLauncherLifetime lifetime)
    {
        lock (_gate)
        {
            if (!_retained.TryGetValue(lifetime.Binding.LauncherId, out var prior) || prior.Recovered is not null || prior.Live is not null && !ReferenceEquals(prior.Live, lifetime))
                throw new IOException("execution_provider_retained_owner_unbound");
            _retained[lifetime.Binding.LauncherId] = new(lifetime, null, true);
            if (_monitorRunning) return;
            _monitorRunning = true;
            _monitor = Task.Run(MonitorRetainedAsync);
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task MonitorRetainedAsync()
    {
        while (!_monitorStopping.IsCancellationRequested)
        {
            ProviderLauncherLifetime[] current;
            lock (_gate)
            {
                current = _retained.Values.Select(entry => entry.Live).OfType<ProviderLauncherLifetime>().ToArray();
                if (current.Length == 0) { _monitorRunning = false; return; }
            }
            foreach (var lifetime in current)
                if (lifetime.TryPublishEnded())
                    lock (_gate) _retained.Remove(lifetime.Binding.LauncherId);
            try { await Task.Delay(250, _monitorStopping.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        lock (_gate) _monitorRunning = false;
    }

    public async Task StopMonitorAsync()
    {
        _monitorStopping.Cancel();
        Task pending; lock (_gate) pending = _monitor;
        await pending.ConfigureAwait(false);
    }


    [SupportedOSPlatform("windows")]
    internal IAsyncDisposable Register(ExtensionScope scope, VerifiedProviderPackage package)
    {
        if (scope.Activation.ExtensionId != package.Manifest.ExtensionId)
            throw new IOException("execution_provider_activation_unbound");
        lock (_gate)
        {
            if (!scope.Activation.IsAccepting) throw new IOException("execution_provider_activation_revoked");
            if (_registrations.Count >= 256) throw new IOException("execution_provider_registration_limit");
            var registration = new Registration(this, scope.Activation, scope.Cancellation, package);
            if (!_registrations.TryAdd(scope.Activation.ExtensionId, registration))
                throw new IOException("execution_provider_already_registered");
            return registration;
        }
    }

    [SupportedOSPlatform("windows")]
    internal PreparedExecutionProvider Acquire(string extensionId)
    {
        Registration registration;
        lock (_gate)
        {
            if (!_registrations.TryGetValue(extensionId, out registration!) || !registration.Activation.IsPublished)
                throw new IOException("execution_extension_provider_unavailable");
        }
        var held = registration.Package.Hold();
        InstallationReadLease? consumption = null;
        var retainedId = registration.Package.Declaration.Launcher is null ? Guid.Empty : Guid.NewGuid();
        try
        {
            Require(registration);
            consumption = registration.Package.OpenConsumptionLease();
            if (retainedId != Guid.Empty) ReserveRetained(retainedId);
            return new(this, registration, held, retainedId, consumption);
        }
        catch { if (retainedId != Guid.Empty) ReleaseReservation(retainedId); consumption?.Dispose(); held.Dispose(); throw; }
    }

    [SupportedOSPlatform("windows")]
    private void Require(Registration registration)
    {
        RequireRegistered(registration);
        registration.Package.RequireConsumptionEligibility();
        RequireRegistered(registration);
    }

    private void RequireRegistered(Registration registration)
    {
        lock (_gate)
            if (!registration.Activation.IsPublished || registration.Token.IsCancellationRequested ||
                !_registrations.TryGetValue(registration.Activation.ExtensionId, out var current) || !ReferenceEquals(current, registration))
                throw new IOException("execution_provider_activation_revoked");
    }

    private void Remove(Registration registration)
    {
        lock (_gate)
            if (_registrations.TryGetValue(registration.Activation.ExtensionId, out var current) && ReferenceEquals(current, registration))
                _registrations.Remove(registration.Activation.ExtensionId);
    }

    private sealed record RetainedEntry(ProviderLauncherLifetime? Live, RetainedResourceReferences? Recovered, bool Claimed);

    [SupportedOSPlatform("windows")]
    public void RestoreSavedReferences(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, ResourceLeaseManager resources)
    {
        foreach (var journal in new RecoveryJournalRegistry(database).ReadSnapshot().Journals.Where(item => item.Kind == NexusPipeline.Core.Configuration.JournalKind.RunAttempt))
            RestoreRetainedReferences(instance, database, contracts, lease, resources, journal.JournalId);
    }

    [SupportedOSPlatform("windows")]
    internal void RestoreRetainedReferences(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        InstanceLease lease, ResourceLeaseManager resources, Guid journalId)
    {
        lease.VerifyHeld(instance);
        var state = AttemptWriterState.Read(instance, database, contracts, journalId);
        var evidence = new ProviderLauncherEvidence(instance, database, contracts);
        _ = evidence.RequireAll(state, verifyContents: true);
        foreach (var item in ProviderLauncherCodec.Read(state.Latest.Root, state.Total, state.Attempt.ExecutionPlan.Sha256)
            .Where(item => item.Stage is "retained" or "recoveredRetained"))
        {
            if (item.Stage == "retained" && ProviderLauncherRows.Ended(database, item.Binding, item.Artifact!) is not null) continue;
            lock (_gate) if (_retained.ContainsKey(item.Binding.LauncherId)) continue;
            var intent = item.Stage == "retained" ? evidence.Read<ProviderLauncherRetained>(item.Artifact!).Intent :
                evidence.Read<ProviderLauncherRecovered>(item.Artifact!).Intent ?? throw new IOException("execution_provider_retention_intent_missing");
            var claims = evidence.Read<ProviderLauncherRetention>(intent).References;
            ProviderLauncherReferences.RequireSubset(state.Total, claims, item.Binding.ExtensionId);
            var operationId = Guid.NewGuid();
            var admission = resources.TryAcquire(operationId, claims);
            using var root = admission.Lease ?? throw new IOException("execution_provider_retained_references_pending");
            var retained = root.RetainReferences(resources, operationId, claims);
            lock (_gate)
            {
                if (_retained.Count >= JournalCapacityLimits.MaximumOwnedJobs)
                { retained.Dispose(); throw new IOException("execution_provider_retained_capacity"); }
                if (!_retained.TryAdd(item.Binding.LauncherId, new(null, retained, true))) retained.Dispose();
            }
        }
    }

    internal sealed class Registration(ExecutionProviderRegistry owner, ExtensionActivation activation,
        CancellationToken token, VerifiedProviderPackage package) : IAsyncDisposable
    {
        internal ExtensionActivation Activation { get; } = activation;
        internal CancellationToken Token { get; } = token;
        internal VerifiedProviderPackage Package { get; } = package;
        public ValueTask DisposeAsync() { owner.Remove(this); return ValueTask.CompletedTask; }
    }

    internal sealed class PreparedExecutionProvider : IDisposable
    {
        private readonly ExecutionProviderRegistry _owner;
        private readonly Registration _registration;
        private readonly object _gate = new();
        private IDisposable? _held;
        private readonly InstallationReadLease? _consumption;
        private readonly object _consumptionGate = new();
        private int _uses;
        private bool _disposed;
        internal Guid RetainedReservationId { get; }
        internal Guid ActivationId => _registration.Activation.ActivationId;
        internal string ExtensionId => _registration.Activation.ExtensionId;
        internal CancellationToken Cancellation => _registration.Token;
        [SupportedOSPlatform("windows")]
        internal VerifiedProviderPackage Package => Use(package => package);

        internal PreparedExecutionProvider(ExecutionProviderRegistry owner, Registration registration, IDisposable held, Guid retainedId,
            InstallationReadLease? consumption)
        { _owner = owner; _registration = registration; _held = held; RetainedReservationId = retainedId; _consumption = consumption; }

        [SupportedOSPlatform("windows")]
        internal T Use<T>(Func<VerifiedProviderPackage, T> action)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _uses++;
            }
            try
            {
                _owner.RequireRegistered(_registration);
                lock (_consumptionGate)
                {
                    if (_consumption is null) _registration.Package.RequireConsumptionEligibility();
                    else _consumption.RequireCurrent();
                }
                _owner.RequireRegistered(_registration);
                return action(_registration.Package);
            }
            finally
            {
                IDisposable? release = null;
                lock (_gate) if (--_uses == 0 && _disposed) release = Interlocked.Exchange(ref _held, null);
                if (release is not null) { _consumption?.Dispose(); release.Dispose(); }
            }
        }

        [SupportedOSPlatform("windows")]
        internal void ClaimRetainedReservation() => Use(_ =>
        {
            lock (_owner._gate)
            {
                if (RetainedReservationId == Guid.Empty || !_owner._retained.TryGetValue(RetainedReservationId, out var entry) ||
                    entry.Live is not null || entry.Recovered is not null || entry.Claimed) throw new IOException("execution_provider_retained_owner_unbound");
                _owner._retained[RetainedReservationId] = entry with { Claimed = true };
            }
            return true;
        });
        internal void ReleaseRetainedReservation(Guid id) => _owner.ReleaseReservation(id);
        [SupportedOSPlatform("windows")]
        internal void KeepRetained(ProviderLauncherLifetime lifetime)
        {
            if (lifetime.Binding.ActivationId != ActivationId || lifetime.Binding.ExtensionId != ExtensionId)
                throw new IOException("execution_provider_retained_owner_unbound");
            _owner.KeepRetained(lifetime);
        }

        public void Dispose()
        {
            IDisposable? release = null;
            lock (_gate)
            {
                _disposed = true;
                if (_uses == 0) release = Interlocked.Exchange(ref _held, null);
            }
            if (RetainedReservationId != Guid.Empty) _owner.ReleaseReservation(RetainedReservationId, onlyUnclaimed: true);
            if (release is not null) { _consumption?.Dispose(); release.Dispose(); }
        }
    }
}
