namespace NexusPipeline.Core.Maintenance;

internal static class PayloadCompletionClaims
{
    internal static IReadOnlyList<ResourceClaim> All { get; } = Array.AsReadOnly<ResourceClaim>([
        new("blobs:content", ResourceClaimMode.Reference),
        new("maintenance:feed", ResourceClaimMode.Reference),
        new("maintenance:host-payload", ResourceClaimMode.Mutation)
    ]);
}
