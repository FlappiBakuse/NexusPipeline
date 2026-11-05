namespace NexusPipeline.Core.Extensions;

public enum ExtensionPayloadObservation { CandidateInstalled, OwnedTargetRemoved, OriginalRestored, Unknown }
public enum ExtensionScopeClosure { NotRequired, Stopped, Unknown }
public enum ExtensionActivationObservation { NotAttempted, PreparedForPublication, Published, PendingRestart, Failed, Unknown }
public enum ExtensionApplyDisposition { CommitInstalled, CommitRemoved, WaitForPublication, WaitForRestart, Failed, Unverified }
public sealed record ExtensionApplyEvidence(ExtensionPayloadObservation Payload, ExtensionScopeClosure PreviousScope,
    ExtensionActivationObservation Activation);
public sealed record ExtensionApplyDecision(ExtensionApplyDisposition Disposition, bool? PayloadIsInstalled,
    bool IsEnabled, string? ReasonCode);

public static class ApplyCompletionPolicy
{
    public static ExtensionApplyDecision Decide(ExtensionApplyPlan plan, ExtensionApplyEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(evidence);
        if (!Enum.IsDefined(evidence.Payload) || !Enum.IsDefined(evidence.PreviousScope) || !Enum.IsDefined(evidence.Activation))
            throw new ExtensionApplyException("extension_apply_evidence_invalid");
        if (plan.MustDeactivateExistingScope
                ? evidence.PreviousScope != ExtensionScopeClosure.Stopped
                : evidence.PreviousScope != ExtensionScopeClosure.NotRequired)
            return Unknown("extension_activity_stop_unverified");
        if (evidence.Payload == ExtensionPayloadObservation.Unknown) return Unknown("extension_payload_unverified");
        if (evidence.Payload == ExtensionPayloadObservation.OriginalRestored)
            return new(ExtensionApplyDisposition.Failed, plan.Original?.IsInstalled ?? false, false, "extension_apply_reverted");
        if (plan.Selection.Action == ExtensionApplyAction.Remove)
            return evidence.Payload == ExtensionPayloadObservation.OwnedTargetRemoved &&
                evidence.Activation == ExtensionActivationObservation.NotAttempted
                ? new(ExtensionApplyDisposition.CommitRemoved, false, false, null)
                : Unknown("extension_remove_unverified");
        if (evidence.Payload != ExtensionPayloadObservation.CandidateInstalled) return Unknown("extension_payload_unverified");
        if (!plan.MustActivateCandidate)
            return evidence.Activation == ExtensionActivationObservation.NotAttempted
                ? new(ExtensionApplyDisposition.CommitInstalled, true, false, null)
                : new(ExtensionApplyDisposition.Unverified, true, false, "extension_disabled_activation_unexpected");
        return evidence.Activation switch
        {
            ExtensionActivationObservation.PreparedForPublication => new(ExtensionApplyDisposition.WaitForPublication, true, false,
                "extension_activation_publication_pending"),
            ExtensionActivationObservation.Published => new(ExtensionApplyDisposition.CommitInstalled, true, true, null),
            ExtensionActivationObservation.PendingRestart => new(ExtensionApplyDisposition.WaitForRestart, true, false, "extension_activation_restart_required"),
            ExtensionActivationObservation.Failed => new(ExtensionApplyDisposition.Failed, true, false, "extension_activation_failed"),
            _ => new(ExtensionApplyDisposition.Unverified, true, false, "extension_activation_unverified")
        };
    }

    private static ExtensionApplyDecision Unknown(string reason) => new(ExtensionApplyDisposition.Unverified, null, false, reason);
}
