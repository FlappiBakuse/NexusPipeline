using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Core.Extensions;

public static class FileEffectPolicy
{
    public static bool IsRevertedScope(RegisteredJournal journal, FileEffect effect) =>
        effect.JournalId == journal.JournalId && journal.Kind == JournalKind.ExtensionApply &&
        journal.Phase is JournalPhase.Reverted or JournalPhase.Committed &&
        effect.State == FileEffectState.Reverted &&
        effect.Action is FileEffectAction.PreserveOriginal or FileEffectAction.InstallCandidate;
}
