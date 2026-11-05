using System.Runtime.Versioning;
using System.Collections.Immutable;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Scheduling;

namespace NexusPipeline.Platform.Storage.Scheduling;

[SupportedOSPlatform("windows")]
internal sealed class AcceptedProviderInvocation
{
    private readonly ScheduledProviderPublication _owner;
    internal ScheduledProviderSnapshot Source { get; }
    internal PublishedWorkEntry Entry { get; }
    internal ExecutionTimeBudget Budget { get; }
    internal AcceptedExecutionRun Activity { get; }
    internal CancellationTokenSource Cancellation { get; } = new();

    private AcceptedProviderInvocation(ScheduledProviderPublication owner, ScheduledProviderSnapshot source,
        PublishedWorkEntry entry, ExecutionTimeBudget budget, AcceptedExecutionRun activity)
    { _owner = owner; Source = source; Entry = entry; Budget = budget; Activity = activity; }

    internal static AcceptedProviderInvocation Accept(ScheduledProviderPublication owner, ResourceOperationLease actualRoot,
        ResourceLeaseManager resources, ScheduledProviderSnapshot source, PublishedWorkEntry entry, ExecutionTimeBudget budget)
    {
        using var held = actualRoot.Borrow(resources, source.Document.OperationId, Claims(entry.ExtensionId));
        return held.Use(() => new AcceptedProviderInvocation(owner, source, entry, budget,
            actualRoot.AcceptRun(resources, source.Document.OperationId, source.Document.Invocation.RunId)));
    }

    internal T Use<T>(ScheduledProviderPublication expected, Func<T> callback)
    {
        if (!ReferenceEquals(expected, _owner) || !Activity.IsActive || Cancellation.IsCancellationRequested)
            throw new IOException("scheduled_provider_dispatch_closed");
        Entry.Use(() => true);
        if (Budget.Read().IsExhausted) throw new IOException("scheduled_provider_deadline");
        return callback();
    }

    internal static ImmutableArray<ResourceClaim> Claims(string extension) =>
        [new("scheduling:state", ResourceClaimMode.Mutation), new("extension:" + extension, ResourceClaimMode.Reference),
            new("notifications:catalog", ResourceClaimMode.Reference), new("secrets:fields", ResourceClaimMode.Mutation),
            new("extension-data:" + extension, ResourceClaimMode.Mutation),
            new("settings:host", ResourceClaimMode.Reference), new("blobs:content", ResourceClaimMode.Reference)];

    internal static ImmutableArray<ResourceClaim> CallbackClaims(string extension) =>
        [new("extension:" + extension, ResourceClaimMode.Reference)];
}
