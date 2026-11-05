using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Journals;

internal static class PayloadFileIntents
{
    internal static IReadOnlyList<FileEffect> Create(FrozenPayloadApply plan, string planDigest)
    {
        FileEffect Effect(string step, FileEffectAction action) => new(plan.JournalId, step, action, FileEffectState.Pending, planDigest, null);
        return plan.Action switch
        {
            PayloadAction.Install => [Effect("install-candidate", FileEffectAction.InstallCandidate)],
            PayloadAction.Upgrade => [Effect("preserve-original", FileEffectAction.PreserveOriginal), Effect("install-candidate", FileEffectAction.InstallCandidate)],
            PayloadAction.Remove when plan.Kind == JournalKind.ExtensionApply => [Effect("preserve-original", FileEffectAction.PreserveOriginal)],
            PayloadAction.Remove => [],
            _ => throw new IOException("payload_action_unrecognized")
        };
    }
}
