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
            var blocked = index.BeginVerification(out var generation);
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
                var admission = evidence.ProjectAdmission(snapshot);
                var published = registry.PublishVerified(snapshot.Revision, checked(blocked.Revision + 1));
                if (evidence is ISnapshotCompletionEvidence)
                    RequireSameInventory(snapshot, published, compareRevision: false);
                index.PublishVerifiedCompletions(generation, blocked.Revision, published.Revision, admission, completions);
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

public interface IRecoveryRegistry
{
    RecoveryRegistrySnapshot ReadSnapshot();
    RecoveryRegistrySnapshot PublishVerified(long expectedRevision, long minimumRevision);
}

public interface IRecoveryEvidence
{
    void VerifyAll(IReadOnlyList<RegisteredJournal> registered);
    IReadOnlyList<ResourceIsolation> ProjectAdmission(RecoveryRegistrySnapshot verified) => verified.Isolations;
}

public interface ISnapshotCompletionEvidence : IRecoveryEvidence
{
    IReadOnlyList<SnapshotCompletionProjection> ReadPendingCompletions(RecoveryRegistrySnapshot verified);
}

public interface IRecoveryPreflight
{
    void VerifyRecognizedAll(IReadOnlyList<RegisteredJournal> registered);
}

public interface IRecoveryExecutionOwnership
{
    IDisposable Begin(IReadOnlyList<RegisteredJournal> journals);
}

public enum JournalRecoveryResult { NotExecuted, Restored, Quarantined, AlreadyCompleted, Completed, PendingConfigurationCompletion }

public interface IJournalRecoveryWork : IDisposable
{
    IReadOnlyList<Guid> MustRunAfter => Array.Empty<Guid>();
    JournalRecoveryResult Execute();
    void IDisposable.Dispose() { }
}

public interface IJournalRecoveryHandler
{
    JournalKind Kind { get; }
    IJournalRecoveryWork Prepare(RegisteredJournal journal);
}

public sealed record RegisteredJournal(Guid JournalId, Guid OperationId, JournalKind Kind, JournalPhase Phase,
    long Revision, string PlanPath, string PlanDigest, IReadOnlyList<string> ResourceKeys);

public sealed record RecoveryRegistrySnapshot(long Revision, bool IsReady,
    IReadOnlyList<RegisteredJournal> Journals, IReadOnlyList<ResourceIsolation> Isolations, IReadOnlyList<FileEffect> FileEffects);

public static class RecoveryRegistryPolicy
{
    public static void Verify(RecoveryRegistrySnapshot snapshot)
    {
        if (snapshot.Revision < 1 || snapshot.Journals.Select(item => item.JournalId).Distinct().Count() != snapshot.Journals.Count)
            throw new InvalidOperationException("recovery_registry_identity_invalid");
        foreach (var journal in snapshot.Journals)
        {
            if (journal.JournalId == Guid.Empty || journal.OperationId == Guid.Empty || journal.Revision < 1 || !Enum.IsDefined(journal.Kind) ||
                !Enum.IsDefined(journal.Phase) || !IsDigest(journal.PlanDigest) || journal.PlanPath != $"blobs/plans/{journal.PlanDigest}.json" ||
                journal.ResourceKeys.Count is 0 or > 4096 || journal.ResourceKeys.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 512) ||
                journal.ResourceKeys.Distinct(StringComparer.Ordinal).Count() != journal.ResourceKeys.Count)
                throw new InvalidOperationException("recovery_registry_journal_invalid");
        }
        var expected = snapshot.Journals.Where(item => item.Phase != JournalPhase.Committed)
            .SelectMany(item => item.ResourceKeys.Select(key => (key, item.JournalId))).ToHashSet();
        var actual = snapshot.Isolations.Select(item => (item.ResourceKey, item.JournalId)).ToArray();
        if (actual.Distinct().Count() != actual.Length || !expected.SetEquals(actual) ||
            snapshot.Isolations.Any(item => string.IsNullOrWhiteSpace(item.ReasonCode) || !Enum.IsDefined(item.Mode)))
            throw new InvalidOperationException("recovery_registry_isolation_incoherent");
        if (snapshot.FileEffects.Select(item => (item.JournalId, item.StepId)).Distinct().Count() != snapshot.FileEffects.Count)
            throw new InvalidOperationException("recovery_registry_effect_identity_invalid");
        var journals = snapshot.Journals.ToDictionary(item => item.JournalId);
        foreach (var effect in snapshot.FileEffects)
        {
            if (!journals.TryGetValue(effect.JournalId, out var journal) || !Enum.IsDefined(effect.Action) || !Enum.IsDefined(effect.State) ||
                string.IsNullOrWhiteSpace(effect.StepId) || effect.StepId.Length > 80 || !IsDigest(effect.IntentDigest) ||
                (effect.State == FileEffectState.Pending ? effect.ReceiptDigest is not null : !IsDigest(effect.ReceiptDigest)))
                throw new InvalidOperationException("recovery_registry_effect_invalid");
            if (effect.State == FileEffectState.Reverted &&
                !NexusPipeline.Core.Extensions.FileEffectPolicy.IsRevertedScope(journal, effect))
                throw new InvalidOperationException("recovery_registry_reverted_effect_unbound");
            if (journal.Kind == JournalKind.FileCleanup && effect.Action != FileEffectAction.RemoveOwned)
                throw new InvalidOperationException("recovery_registry_cleanup_action_invalid");
            if (journal.Phase == JournalPhase.Committed && (journal.Kind == JournalKind.FileCleanup || effect.Action != FileEffectAction.RemoveOwned) &&
                effect.State is not (FileEffectState.Completed or FileEffectState.NotExecuted) &&
                !NexusPipeline.Core.Extensions.FileEffectPolicy.IsRevertedScope(journal, effect))
                throw new InvalidOperationException("recovery_registry_terminal_effect_unclosed");
        }
    }

    private static bool IsDigest(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
