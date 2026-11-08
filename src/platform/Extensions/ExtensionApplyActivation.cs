using System.Runtime.Versioning;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Extensions;

[SupportedOSPlatform("windows")]
internal sealed class ExtensionApplyActivation : IAsyncDisposable
{
    private readonly ExtensionActivator.ApplyReservation _inventory;
    private readonly PreparedExtensionActivation _source;
    private readonly ExtensionScope _scope;
    private readonly Task _callback;
    private bool _published;
    private bool _committed;
    private int _disposed;

    private ExtensionApplyActivation(ExtensionActivator.ApplyReservation inventory,
        PreparedExtensionActivation source, ExtensionScope scope, Task callback)
    { _inventory = inventory; _source = source; _scope = scope; _callback = callback; }

    internal Guid ActivationId => _scope.ActivationId;
    internal PreparedExtensionActivation Source => _source;

    internal static async ValueTask<ExtensionApplyActivation> PrepareAsync(ExtensionActivator.ApplyReservation inventory,
        PreparedExtensionActivation source, ContractReader contracts, ExecutionProviderRegistry? providers, InstalledPortFactory? ports)
    {
        source.RequireReservation(inventory); source.RequireBeforeCallback();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scope = inventory.BeginCandidate(source, completion.Task);
        VerifiedProviderPackage.Prepared? provider = null;
        Task runner;
        try
        {
            source.RegisterCallback(scope, completion.Task);
            if (source.ReadManifest().Execution is not null)
                provider = VerifiedProviderPackage.VerifyInstalled(source, contracts);
            runner = Task.Run(async () =>
            {
                try
                {
                    using (provider)
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(source.AcceptedCancellation, scope.Cancellation);
                        using (scope.BeginActivationUse())
                        {
                            source.RequireBeforeCallback();
                            var runtime = InstalledExtensionRuntime.Load(source, scope, contracts, providers, ports, inventory.Native);
                            await scope.OwnAsync(runtime).ConfigureAwait(false);
                            inventory.AttachRuntime(scope, runtime);
                            await runtime.ActivateAsync(scope, linked.Token, provider).ConfigureAwait(false);
                            source.RequireForPreparation(scope.ActivationId);
                        }
                    }
                    completion.TrySetResult();
                }
                catch (Exception failure) { completion.TrySetException(failure); }
                finally { inventory.CandidateEnded(); }
            });
        }
        catch (Exception preparationFailure)
        {
            provider?.Dispose();
            completion.TrySetException(preparationFailure); Observe(completion.Task);
            source.RecordCallbackFailure(completion.Task, preparationFailure);
            await scope.DisposeAsync().ConfigureAwait(false); inventory.CandidateEnded(); throw;
        }
        Observe(runner);
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), source.AcceptedCancellation).ConfigureAwait(false);
            inventory.RequireCallbackReturned(scope);
            return new(inventory, source, scope, completion.Task);
        }
        catch (Exception activationFailure)
        {
            source.RecordCallbackFailure(completion.Task, activationFailure);
            var cleanup = source.DisposeAsync().AsTask(); Observe(cleanup);
            try { await cleanup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception cleanupFailure)
            { throw new AggregateException("extension_activation_cleanup_pending", activationFailure, cleanupFailure); }
            throw;
        }
    }

    internal void RequirePrepared()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _inventory.RequireCallbackReturned(_scope); _source.RequireForPreparation(ActivationId);
        if (_published || _scope.Activation.IsPublished) throw new IOException("extension_candidate_already_published");
    }

    internal void RequirePreparedInsidePublication()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _inventory.RequireCallbackReturned(_scope); _source.RequireLifetime();
        if (_published || _scope.Activation.IsPublished) throw new IOException("extension_candidate_already_published");
    }

    internal void Publish(BlobReference preparation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _inventory.PublishCandidate(_source, _scope, preparation);
        _published = true;
    }

    internal void PublishInstalled()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _inventory.PublishInstalledCandidate(_source, _scope);
        _published = true;
    }

    internal void RequirePublishedInsidePublication()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _inventory.RequireCallbackReturned(_scope); _source.RequireLifetime();
        if (!_published || !_callback.IsCompletedSuccessfully || !_scope.Activation.IsPublished || !_scope.Activation.IsAccepting)
            throw new IOException("extension_candidate_publication_unconfirmed");
    }

    internal BoundExtensionPayload CompleteCommitted()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _source.RequireOwnerHeld();
        var committed = _inventory.AcceptCandidateCommit(_source, _scope);
        _committed = true;
        return committed;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (!_committed)
            {
                var cleanup = _scope.DisposeAsync().AsTask(); Observe(cleanup);
                try { await Task.WhenAll(cleanup, _callback).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                finally { _inventory.CandidateEnded(); }
            }
        }
        finally
        {
            var release = _source.DisposeAsync().AsTask(); Observe(release);
            await release.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    private static void Observe(Task task) => _ = task.ContinueWith(static failure => { _ = failure.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
