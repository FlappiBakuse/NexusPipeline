using System.Collections.Immutable;
using NexusPipeline.Core.Execution;
using NexusPipeline.Core.Maintenance;
using NexusPipeline.Core.Scheduling;

namespace NexusPipeline.Platform.Storage.Scheduling;

internal sealed class AcceptedManualWork : IDisposable
{
    private readonly ManualWorkStore _owner;
    internal ManualWorkSnapshot Source { get; }
    internal PublishedWorkEntry Entry { get; }
    internal ExecutionTimeBudget Budget { get; }
    internal AcceptedExecutionRun Activity { get; }
    internal CancellationTokenSource Cancellation { get; } = new();

    internal AcceptedManualWork(ManualWorkStore owner, ManualWorkSnapshot source, PublishedWorkEntry entry,
        ExecutionTimeBudget budget, AcceptedExecutionRun activity)
    { _owner = owner; Source = source; Entry = entry; Budget = budget; Activity = activity; }

    internal void RequireOwner(ManualWorkStore owner)
    {
        if (!ReferenceEquals(owner, _owner) || !Activity.IsActive) throw new UnauthorizedAccessException("manual_work_owner_unbound");
    }

    internal static ImmutableArray<ResourceClaim> Claims(string extension) =>
        [new("scheduling:state", ResourceClaimMode.Mutation), new("extension:" + extension, ResourceClaimMode.Reference),
            new("extension-data:" + extension, ResourceClaimMode.Mutation), new("secrets:fields", ResourceClaimMode.Mutation),
            new("notifications:catalog", ResourceClaimMode.Reference), new("settings:host", ResourceClaimMode.Reference),
            new("blobs:content", ResourceClaimMode.Reference)];

    internal static ImmutableArray<ResourceClaim> MetadataClaims(string extension) =>
        [new("scheduling:state", ResourceClaimMode.Mutation), new("extension:" + extension, ResourceClaimMode.Reference)];

    public void Dispose() { Activity.Dispose(); Cancellation.Dispose(); }
}
