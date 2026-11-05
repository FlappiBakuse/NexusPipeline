using NexusPipeline.Core.Maintenance;
using NexusPipeline.Platform.Storage.Extensions;

namespace NexusPipeline.Platform.Storage.Journals;

internal static class FileCleanupItems
{
    internal static IReadOnlyList<string> Steps(FrozenFileCleanup plan) => plan.ExtensionWork is { } work
        ? Array.AsReadOnly(work.Page.Indices.Select((_, index) => $"remove-{index:000}").ToArray())
        : Array.AsReadOnly(plan.Items.Select(item => item.StepId).ToArray());

    internal static IReadOnlyList<string> Keys(FrozenFileCleanup plan) => plan.ExtensionWork is { } work
        ? Array.AsReadOnly(ExtensionWorkClaims.Read(work.Origin).Select(item => item.ResourceKey).ToArray())
        : Array.AsReadOnly(plan.Items.Select(item => CleanupCandidateFiles.ResourceKey(item.Blob)).Order(StringComparer.Ordinal).ToArray());

    internal static IReadOnlyList<ResourceClaim> Claims(FrozenFileCleanup plan) => plan.ExtensionWork is { } work
        ? ExtensionWorkClaims.Read(work.Origin)
        : Array.AsReadOnly(Keys(plan).Select(key => new ResourceClaim(key, ResourceClaimMode.Mutation)).ToArray());

    internal static ExtensionWorkNode WorkNode(FrozenFileCleanup plan, string step)
    {
        var work = plan.ExtensionWork ?? throw new IOException("extension_work_plan_required");
        var index = Steps(plan).ToList().FindIndex(value => value == step);
        return index >= 0 ? work.Origin.Proof.Nodes[work.Page.Indices[index]] : throw new IOException("extension_work_step_unbound");
    }
}
