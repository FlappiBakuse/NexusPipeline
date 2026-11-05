namespace NexusPipeline.Core.Configuration;

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
