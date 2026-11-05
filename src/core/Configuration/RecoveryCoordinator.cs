namespace NexusPipeline.Core.Configuration;

public sealed class RecoveryCoordinator(IRecoveryRegistry registry, IRecoveryEvidence evidence, RecoveryIndex index,
    IRecoveryExecutionOwnership? ownership = null)
{
    private readonly object _gate = new();

    public IReadOnlyDictionary<Guid, JournalRecoveryResult> RecoverAndPublish(IRecoveryPreflight preflight,
        IReadOnlyList<IJournalRecoveryHandler> handlers)
    {
        lock (_gate)
        {
            index.BlockUnknown("recovery_preflight_in_progress");
            try
            {
                if (handlers.Select(handler => handler.Kind).Distinct().Count() != handlers.Count ||
                    !handlers.Select(handler => handler.Kind).ToHashSet().SetEquals(Enum.GetValues<JournalKind>()))
                    throw new InvalidOperationException("recovery_kind_handlers_incomplete");
                var byKind = handlers.ToDictionary(handler => handler.Kind);
                var snapshot = registry.ReadSnapshot();
                RecoveryRegistryPolicy.Verify(snapshot);
                preflight.VerifyRecognizedAll(snapshot.Journals);
                var prepared = new List<(RegisteredJournal Journal, IJournalRecoveryWork Work)>();
                try
                {
                    foreach (var journal in snapshot.Journals) prepared.Add((journal, byKind[journal.Kind].Prepare(journal)));
                    if (registry.ReadSnapshot().Revision != snapshot.Revision) throw new InvalidOperationException("recovery_preflight_revision_changed");
                    var ordered = OrderWork(prepared);
                    using var executionOwner = ownership?.Begin(snapshot.Journals);
                    var results = new Dictionary<Guid, JournalRecoveryResult>();
                    foreach (var item in ordered) results.Add(item.Journal.JournalId, item.Work.Execute());
                    VerifyAndPublish();
                    return new System.Collections.ObjectModel.ReadOnlyDictionary<Guid, JournalRecoveryResult>(results);
                }
                finally { ReleasePrepared(prepared); }
            }
            catch
            {
                index.BlockUnknown("recovery_evidence_unverified");
                throw;
            }
        }
    }

    private static IReadOnlyList<(RegisteredJournal Journal, IJournalRecoveryWork Work)> OrderWork(
        List<(RegisteredJournal Journal, IJournalRecoveryWork Work)> prepared)
    {
        var positions = prepared.Select((item, position) => (item.Journal.JournalId, position))
            .ToDictionary(item => item.JournalId, item => item.position);
        var pending = new int[prepared.Count];
        var next = Enumerable.Range(0, prepared.Count).Select(_ => new List<int>()).ToArray();
        for (var position = 0; position < prepared.Count; position++)
        {
            var dependencies = prepared[position].Work.MustRunAfter?.ToArray()
                ?? throw new InvalidOperationException("recovery_dependency_invalid");
            if (dependencies.Length > prepared.Count || dependencies.Distinct().Count() != dependencies.Length)
                throw new InvalidOperationException("recovery_dependency_invalid");
            foreach (var id in dependencies)
            {
                if (!positions.TryGetValue(id, out var predecessor) || predecessor == position)
                    throw new InvalidOperationException("recovery_dependency_invalid");
                pending[position]++; next[predecessor].Add(position);
            }
        }
        var ready = new PriorityQueue<int, int>();
        for (var position = 0; position < pending.Length; position++)
            if (pending[position] == 0) ready.Enqueue(position, position);
        var ordered = new List<(RegisteredJournal Journal, IJournalRecoveryWork Work)>(prepared.Count);
        while (ready.TryDequeue(out var position, out _))
        {
            ordered.Add(prepared[position]);
            foreach (var dependent in next[position])
                if (--pending[dependent] == 0) ready.Enqueue(dependent, dependent);
        }
        if (ordered.Count != prepared.Count) throw new InvalidOperationException("recovery_dependency_cycle");
        return ordered;
    }

    private static void ReleasePrepared(List<(RegisteredJournal Journal, IJournalRecoveryWork Work)> prepared)
    {
        List<Exception>? failures = null;
        for (var position = prepared.Count - 1; position >= 0; position--)
        {
            try { prepared[position].Work.Dispose(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is not null) throw new AggregateException("recovery_preparation_release_failed", failures);
    }

    public RecoveryProjection VerifyAndPublish()
    {
        lock (_gate)
        {
            var blocked = index.BlockUnknown("recovery_verification_in_progress");
            try
            {
                var snapshot = registry.ReadSnapshot();
                RecoveryRegistryPolicy.Verify(snapshot);
                if (evidence is ISnapshotCompletionEvidence) snapshot = FreezeInventory(snapshot);
                evidence.VerifyAll(snapshot.Journals);
                var completions = System.Collections.Immutable.ImmutableArray<SnapshotCompletionProjection>.Empty;
                if (evidence is ISnapshotCompletionEvidence snapshotEvidence)
                {
                    RequireSameInventory(snapshot, registry.ReadSnapshot(), compareRevision: true);
                    completions = SnapshotCompletionProjection.FreezeAll(snapshotEvidence.ReadPendingCompletions(snapshot), snapshot);
                    RequireSameInventory(snapshot, registry.ReadSnapshot(), compareRevision: true);
                }
                var published = registry.PublishVerified(snapshot.Revision, checked(blocked.Revision + 1));
                if (evidence is ISnapshotCompletionEvidence)
                    RequireSameInventory(snapshot, published, compareRevision: false);
                index.PublishVerifiedCompletions(blocked.Revision, published.Revision, published.Isolations, completions);
                return index.Current;
            }
            catch
            {
                index.BlockUnknown("recovery_evidence_unverified");
                throw;
            }
        }
    }

    private static void RequireSameInventory(RecoveryRegistrySnapshot before, RecoveryRegistrySnapshot after, bool compareRevision)
    {
        RecoveryRegistryPolicy.Verify(after);
        var journals = before.Journals.ToDictionary(item => item.JournalId);
        if (compareRevision && before.Revision != after.Revision || !compareRevision && (!after.IsReady || after.Revision <= before.Revision) ||
            before.Journals.Count != after.Journals.Count || after.Journals.Any(item =>
            !journals.TryGetValue(item.JournalId, out var prior) || item.OperationId != prior.OperationId || item.Kind != prior.Kind ||
            item.Phase != prior.Phase || item.Revision != prior.Revision || item.PlanPath != prior.PlanPath || item.PlanDigest != prior.PlanDigest ||
            !item.ResourceKeys.SequenceEqual(prior.ResourceKeys)) ||
            !before.Isolations.ToHashSet().SetEquals(after.Isolations) || !before.FileEffects.ToHashSet().SetEquals(after.FileEffects))
            throw new InvalidOperationException("snapshot_completion_registry_changed");
    }

    private static RecoveryRegistrySnapshot FreezeInventory(RecoveryRegistrySnapshot snapshot) => snapshot with
    {
        Journals = Array.AsReadOnly(snapshot.Journals.Select(item => item with
            { ResourceKeys = Array.AsReadOnly(item.ResourceKeys.ToArray()) }).ToArray()),
        Isolations = Array.AsReadOnly(snapshot.Isolations.ToArray()),
        FileEffects = Array.AsReadOnly(snapshot.FileEffects.ToArray())
    };
}
