namespace NexusPipeline.Core.Configuration;

public enum JournalKind { ConfigExchange, ConfigEdit, FileCleanup, RunAttempt, UpdateApply, ExtensionApply, AssetPrepare, SecretPrepare }
public enum JournalPhase { Prepared, Applying, Applied, Reverting, Reverted, Committed, Quarantined }
public enum TransitionRequirement { Forbidden, None, CompleteNoEffectEvidence, VerifiedRecoveryEvidence }

public sealed record NoEffectEvidence(
    bool HasCompleteInventory,
    bool HasExternalWrites,
    bool HasStartedWriter,
    bool HasUnknownBytes);

public static class JournalTransitionPolicy
{
    public static bool CanMove(JournalKind kind, JournalPhase from, JournalPhase to,
        NoEffectEvidence? noEffect = null, bool hasVerifiedRecoveryEvidence = false)
        => Requirement(kind, from, to) switch
        {
            TransitionRequirement.None => true,
            TransitionRequirement.CompleteNoEffectEvidence => noEffect is { HasCompleteInventory: true, HasExternalWrites: false,
                HasStartedWriter: false, HasUnknownBytes: false },
            TransitionRequirement.VerifiedRecoveryEvidence => hasVerifiedRecoveryEvidence,
            _ => false
        };

    public static TransitionRequirement Requirement(JournalKind kind, JournalPhase from, JournalPhase to)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(from) || !Enum.IsDefined(to) || from == to)
            return TransitionRequirement.Forbidden;
        if (from == JournalPhase.Committed)
            return TransitionRequirement.Forbidden;
        if (to == JournalPhase.Quarantined)
            return TransitionRequirement.None;
        if (from == JournalPhase.Prepared && to == JournalPhase.Committed)
            return TransitionRequirement.CompleteNoEffectEvidence;
        if (from == JournalPhase.Quarantined)
        {
            var recoveryTarget = kind switch
            {
                JournalKind.ConfigExchange or JournalKind.ConfigEdit or JournalKind.UpdateApply or JournalKind.ExtensionApply
                    => to == JournalPhase.Reverting,
                JournalKind.FileCleanup => to == JournalPhase.Applying,
                JournalKind.RunAttempt => to == JournalPhase.Applied,
                _ => false
            };
            return recoveryTarget ? TransitionRequirement.VerifiedRecoveryEvidence : TransitionRequirement.Forbidden;
        }
        if ((kind is JournalKind.ConfigExchange or JournalKind.ConfigEdit && from == JournalPhase.Prepared && to == JournalPhase.Reverting) ||
            (kind is JournalKind.ConfigExchange or JournalKind.ConfigEdit or JournalKind.UpdateApply or JournalKind.ExtensionApply &&
             from == JournalPhase.Applying && to == JournalPhase.Reverting) ||
            (kind is JournalKind.UpdateApply or JournalKind.ExtensionApply && from == JournalPhase.Applied && to == JournalPhase.Reverting))
            return TransitionRequirement.VerifiedRecoveryEvidence;
        var allowed = kind switch
        {
            JournalKind.ConfigExchange or JournalKind.ConfigEdit => (from, to) switch
            {
                (JournalPhase.Prepared, JournalPhase.Applying) => true,
                (JournalPhase.Applying, JournalPhase.Applied) => true,
                (JournalPhase.Applied, JournalPhase.Reverting) => true,
                (JournalPhase.Reverting, JournalPhase.Reverted) => true,
                (JournalPhase.Reverted, JournalPhase.Committed) => true,
                _ => false
            },
            JournalKind.FileCleanup or JournalKind.RunAttempt or JournalKind.AssetPrepare or JournalKind.SecretPrepare => (from, to) switch
            {
                (JournalPhase.Prepared, JournalPhase.Applying) => true,
                (JournalPhase.Applying, JournalPhase.Applied) => true,
                (JournalPhase.Applied, JournalPhase.Committed) => true,
                _ => false
            },
            JournalKind.UpdateApply or JournalKind.ExtensionApply => (from, to) switch
            {
                (JournalPhase.Prepared, JournalPhase.Applying) => true,
                (JournalPhase.Applying, JournalPhase.Applied) => true,
                (JournalPhase.Applied, JournalPhase.Committed) => true,
                (JournalPhase.Reverting, JournalPhase.Reverted) => true,
                (JournalPhase.Reverted, JournalPhase.Committed) => true,
                _ => false
            },
            _ => false
        };
        return allowed ? TransitionRequirement.None : TransitionRequirement.Forbidden;
    }
}
