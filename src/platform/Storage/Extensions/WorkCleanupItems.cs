using System.Text.Json.Nodes;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed class WorkCleanupItems
{
    private readonly FrozenFileCleanup _plan;
    private readonly ExtensionWorkExecution? _execution;
    private readonly IReadOnlySet<int> _previous;
    private IReadOnlySet<int> Previous => _execution?.ProtectedPrevious ?? _previous;

    internal WorkCleanupItems(RuntimeInstance instance, StateDatabase database, ContractReader contracts,
        PayloadApplyRoots roots, FrozenFileCleanup plan, ExtensionWorkExecution? execution)
    {
        _plan = plan; _execution = execution;
        _previous = new ExtensionWorkHistory(instance, database, contracts, roots).RequirePage(plan.ExtensionWork!);
    }

    internal IReadOnlyList<CleanupItemInspection> InspectItems(JsonObject journal)
    {
        var steps = FileCleanupItems.Steps(_plan);
        try
        {
            Require(journal);
            var states = steps.ToDictionary(step => step, _ => CleanupItemState.VerifiedUnreferenced, StringComparer.Ordinal);
            foreach (var receipt in journal["receipts"]!.AsArray())
            {
                var step = receipt!["stepId"]!.GetValue<string>();
                if (!states.ContainsKey(step)) continue;
                states[step] = receipt["effect"]!.GetValue<string>() switch
                {
                    "completed" => CleanupItemState.DeletedWithEvidence,
                    "notExecuted" => CleanupItemState.SkippedWithEvidence,
                    _ => CleanupItemState.VerifiedUnreferenced
                };
            }
            return steps.Select(step => new CleanupItemInspection(step, states[step])).ToArray();
        }
        catch (IOException) { return steps.Select(step => new CleanupItemInspection(step, CleanupItemState.Unknown)).ToArray(); }
    }

    internal CleanupStepResult Remove(string step, JsonObject journal)
    {
        Require(journal);
        var node = FileCleanupItems.WorkNode(_plan, step);
        var removed = ExtensionWorkInventory.Removed(_plan, journal, Previous);
        var first = _plan.ExtensionWork!.Origin.Proof.Nodes.FirstOrDefault(value => !removed.Contains(value.Index));
        if (first?.Index != node.Index) throw new IOException("extension_work_removal_order_invalid");
        var path = WorkNodeDerivation.Resolve(_plan.ExtensionWork.Origin.Proof, node);
        if (node.Kind == FileObjectKind.File) PhysicalObjectIdentity.RequireSingleLink(path);
        PhysicalObjectDeletion.DeleteVerified(new(path, node.ParentIdentity, node.Identity, node.Kind, node.SizeBytes, node.Sha256));
        _execution!.Removed(node);
        return CleanupStepResult.Deleted;
    }

    internal void Publish(Action action)
    {
        if (_execution is null) throw new IOException("extension_work_cleanup_authority_required");
        _execution.Publish(_plan, action);
    }

    private void Require(JsonObject journal)
    {
        if (_execution is null) throw new IOException("extension_work_cleanup_authority_required");
        _execution.Require(_plan);
        _execution.RequireInventory(_plan, journal, Previous);
    }
}
