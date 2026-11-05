using System.Runtime.Versioning;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Core.Extensions;
using NexusPipeline.Platform.Extensions;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record PayloadApplyResult(Guid OperationId, Guid JournalId, long InstalledRevision,
    bool IsInstalled, bool IsEnabled, Guid? ActivationId);

[SupportedOSPlatform("windows")]
internal sealed class PayloadApplySession(RuntimeInstance instance, StateDatabase database,
    InstanceLease lease, ContractReader contracts, PayloadApplyRoots roots,
    ExtensionActivator activator, IPayloadPersistenceObserver? observer = null)
{
    internal async ValueTask<PayloadApplyResult> ApplyAsync(PreparedExtensionPayload prepared,
        CancellationToken acceptedCancellation)
    {
        lease.VerifyHeld(instance);
        if (prepared.ReadCommitted() is { } committed) return Result(committed);
        var root = prepared.RequireReservationRoot();
        using var inventory = await activator.ReserveForApplyAsync(root, prepared.ExtensionId, acceptedCancellation).ConfigureAwait(false);
        _ = new ExtensionPhysicalJournal(instance, database, contracts, roots, observer)
            .Apply(prepared, inventory, acceptedCancellation);
        ExtensionApplyActivation? activation = null;
        PreparedExtensionActivation? source = null;
        Exception? operationFailure = null;
        try
        {
            var required = prepared.WithPhysical(inventory, acceptedCancellation,
                (_, _, _, _, policy, _, _, _) => policy.MustActivateCandidate);
            if (required)
            {
                source = PreparedExtensionActivation.Prepare(instance, database, lease, contracts, roots,
                    prepared, inventory, acceptedCancellation);
                activation = await inventory.PrepareCandidateAsync(source).ConfigureAwait(false);
            }
            var store = new PayloadCommitStore(instance, database, contracts, roots, observer);
            if (activation is not null)
            {
                var reference = store.PrepareActivation(prepared, inventory, activation, acceptedCancellation);
                activation.Publish(reference);
            }
            var bound = store.Commit(prepared, inventory, activation, acceptedCancellation);
            if (activation is not null) bound = activation.CompleteCommitted();
            return Result(bound);
        }
        catch (Exception failure)
        {
            operationFailure = failure;
            if (activation is null && source is not null)
            {
                try
                {
                    var cleanup = source.DisposeAsync().AsTask();
                    _ = cleanup.ContinueWith(static fault => { _ = fault.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    await cleanup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    using var failed = source.CaptureFailedCallback();
                    if (failed is not null)
                        _ = new ExtensionPayloadRestoration(instance, database, contracts, roots, lease,
                            new JournalRecoveryOwnership(database), observer).RecoverFailedCallback(failed);
                }
                catch (Exception restorationFailure)
                { throw new AggregateException("extension_activation_restoration_pending", failure, restorationFailure); }
            }
            throw;
        }
        finally
        {
            try
            {
                try
                {
                    if (activation is not null) await activation.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    if (source is not null)
                    {
                        var cleanup = source.DisposeAsync().AsTask();
                        _ = cleanup.ContinueWith(static fault => { _ = fault.Exception; }, CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                        await cleanup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception cleanupFailure) when (operationFailure is not null)
            { throw new AggregateException("extension_apply_cleanup_pending", operationFailure, cleanupFailure); }
        }
    }

    private PayloadApplyResult Result(BoundExtensionPayload bound)
    {
        var commit = bound.Commit ?? throw new IOException("extension_installation_commit_pending");
        var enabled = commit.ActivationId is { } activationId && activator.IsPublished(bound.Source.ExtensionId, activationId);
        if (bound.Registered.Phase != JournalPhase.Committed || bound.Footprint is null ||
            enabled && (commit.ActivationState != "published" || commit.ActivationId is null))
            throw new IOException("extension_installation_commit_pending");
        return new(bound.Plan.OperationId, bound.Plan.JournalId, commit.InstalledRevision,
            bound.Footprint.IsInstalled, enabled, enabled ? commit.ActivationId : null);
    }
}
