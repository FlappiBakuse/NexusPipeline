using System.Collections.Immutable;
using NexusPipeline.Core.Configuration;
using NexusPipeline.Platform.Serialization;
using NexusPipeline.Platform.Storage.Journals;
using NexusPipeline.Platform.Storage.Sqlite;

namespace NexusPipeline.Platform.Storage.Extensions;

internal sealed record WorkHistoryEntry(RegisteredJournal Registered, BoundFileCleanup Bound,
    IReadOnlyList<FileEffect> Effects);

internal sealed class ExtensionWorkHistory(RuntimeInstance instance, StateDatabase database, ContractReader contracts, PayloadApplyRoots roots)
{
    internal const int MaximumPages = 256;

    internal ImmutableHashSet<int> Completed(BoundExtensionWork origin, Guid? previous, BlobReference? reference)
    {
        return Completed(origin, previous, reference, null);
    }

    private ImmutableHashSet<int> Completed(BoundExtensionWork origin, Guid? previous, BlobReference? reference,
        List<WorkHistoryEntry>? evidence)
    {
        var chain = new List<(RegisteredJournal Registered, BoundWorkPage Work, BoundFileCleanup Bound, IReadOnlyList<FileEffect> Effects)>();
        var seen = new HashSet<Guid>();
        while (previous is { } id)
        {
            if (reference is null || !seen.Add(id) || chain.Count >= MaximumPages) throw Invalid();
            var registered = new RecoveryJournalRegistry(database).ReadJournal(id);
            if (registered.Phase != JournalPhase.Committed || registered.PlanPath != reference.RelativePath ||
                registered.PlanDigest != reference.Sha256) throw Invalid();
            var effects = new FileEffectOutbox(database).Read(id);
            var bound = CleanupJournalBinding.ReadHistoryPage(instance, contracts, registered, effects, database, roots);
            var work = bound.Plan.ExtensionWork ?? throw Invalid();
            if (work.Origin.Reference != origin.Reference || bound.Document["phase"]!.GetValue<string>() != "committed" ||
                bound.Document["revision"]!.GetValue<long>() != registered.Revision) throw Invalid();
            chain.Add((registered, work, bound, effects));
            previous = work.Page.PreviousJournalId; reference = work.Page.PreviousPlan;
        }
        if (reference is not null) throw Invalid();
        var removed = ImmutableHashSet.CreateBuilder<int>();
        foreach (var item in chain.AsEnumerable().Reverse())
        {
            RequireNext(origin, removed, item.Work.Page.Indices);
            var effects = item.Effects;
            evidence?.Add(new(item.Registered, item.Bound, effects));
            for (var index = 0; index < item.Work.Page.Indices.Length; index++)
            {
                var effect = effects.Single(value => value.StepId == $"remove-{index:000}");
                if (effect.State == FileEffectState.Completed) removed.Add(item.Work.Page.Indices[index]);
                else if (effect.State != FileEffectState.NotExecuted) throw Invalid();
            }
        }
        return removed.ToImmutable();
    }

    internal ImmutableHashSet<int> RequirePage(BoundWorkPage work)
    {
        var completed = Completed(work.Origin, work.Page.PreviousJournalId, work.Page.PreviousPlan);
        RequireNext(work.Origin, completed, work.Page.Indices);
        return completed;
    }

    internal IReadOnlyList<WorkHistoryEntry> Capture(BoundWorkPage work)
    {
        var result = new List<WorkHistoryEntry>();
        var completed = Completed(work.Origin, work.Page.PreviousJournalId, work.Page.PreviousPlan, result);
        RequireNext(work.Origin, completed, work.Page.Indices);
        return result.AsReadOnly();
    }

    private static void RequireNext(BoundExtensionWork origin, IEnumerable<int> completed, ImmutableArray<int> indices)
    {
        var removed = completed.ToHashSet();
        if (!indices.SequenceEqual(origin.Proof.Nodes.Select(item => item.Index).Where(index => !removed.Contains(index)).Take(indices.Length)))
            throw Invalid();
    }
    private static IOException Invalid() => new("extension_work_cleanup_history_unbound");
}
