using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Sqlite;

internal sealed record RecoveryCapacityBudget(int Journals, int FileEffects, int Jobs)
{
    internal const int MaximumJournals = 10000, MaximumFileEffects = 65536, MaximumJobs = 10000;

    internal void RequireValid()
    {
        if (Journals is < 1 or > MaximumJournals || FileEffects is < 0 or > MaximumFileEffects || Jobs is < 0 or > MaximumJobs)
            throw new IOException("recovery_capacity_exceeded");
    }

    internal static RecoveryCapacityBudget ForJournal(RegisteredJournal journal, IReadOnlyList<FileEffect> effects)
    {
        var reverse = journal.Kind is JournalKind.UpdateApply or JournalKind.ExtensionApply
            ? effects.Count(effect => effect.Action is FileEffectAction.PreserveOriginal or FileEffectAction.InstallCandidate) : 0;
        // Update recovery may register a failed candidate and then restart the original Host.
        return new(1, checked(effects.Count + reverse), journal.Kind == JournalKind.UpdateApply ? 3 : 1);
    }
}
