using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Journals;

internal static class ConfigurationFileEffects
{
    internal static FileEffect[] Create(Guid journalId, string planDigest) => new[] {
        ("preserve-original", FileEffectAction.PreserveOriginal), ("install-candidate", FileEffectAction.InstallCandidate),
        ("retire-candidate", FileEffectAction.RetireCandidate), ("restore-original", FileEffectAction.RestoreOriginal)
    }.Select(item => new FileEffect(journalId, item.Item1, item.Item2, FileEffectState.Pending, planDigest, null)).ToArray();
}
