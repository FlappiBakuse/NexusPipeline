using System.Collections.Immutable;
using NexusPipeline.Core.Automation;

namespace NexusPipeline.Core.Execution;

public sealed record FrozenCompletionIntent(Guid RunId, Guid AttemptId, Guid QueueId, long QueueRevision,
    AutomationCompletionAction Action, string PlanDigest, string SourceReceiptDigest);
public sealed record CompletionActionDecision(bool IsEligible, string ReasonCode, TimeSpan Countdown);
public sealed record CompletionActionGroup(AutomationCompletionAction Action, ImmutableArray<FrozenCompletionIntent> Intents);

public static class CompletionActionPolicy
{
    public static CompletionActionDecision Evaluate(FrozenCompletionIntent intent, AttemptReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(intent); ArgumentNullException.ThrowIfNull(receipt);
        if (!Valid(intent) || receipt.Facts.RunId != intent.RunId || receipt.Facts.AttemptId != intent.AttemptId ||
            receipt.PlanDigest != intent.PlanDigest || receipt.SourceReceiptDigest != intent.SourceReceiptDigest)
            throw new ArgumentException("completion_intent_unbound");
        if (intent.Action == AutomationCompletionAction.None) return new(false, "completion_none", TimeSpan.Zero);
        if (receipt.Facts.Lifecycle != LifecycleOutcome.Completed)
            return new(false, "completion_lifecycle_not_completed", TimeSpan.Zero);
        if (receipt.Facts.Recovery is not (RecoveryOutcome.NotRequired or RecoveryOutcome.Restored))
            return new(false, "completion_recovery_pending", TimeSpan.Zero);
        // A business failure does not undo a completed, fully restored execution.
        return new(true, "completion_eligible", intent.Action == AutomationCompletionAction.Exit ? TimeSpan.Zero : TimeSpan.FromSeconds(60));
    }

    public static CompletionActionGroup Combine(IReadOnlyList<FrozenCompletionIntent> intents)
    {
        ArgumentNullException.ThrowIfNull(intents);
        var frozen = intents.ToImmutableArray();
        if (frozen.IsDefaultOrEmpty || frozen.Length > 4096 || frozen.Any(intent => intent is null || !Valid(intent)) ||
            frozen.Select(intent => intent.RunId).Distinct().Count() != frozen.Length)
            throw new ArgumentException("completion_group_invalid");
        var actions = frozen.Where(intent => intent.Action != AutomationCompletionAction.None).Select(intent => intent.Action).Distinct().ToArray();
        if (actions.Length > 1) throw new InvalidOperationException("completion_action_conflict");
        return new(actions.SingleOrDefault(), frozen);
    }

    public static bool CanCancel(AutomationCompletionAction action)
    {
        if (!Enum.IsDefined(action)) throw new ArgumentException("completion_action_invalid");
        return action is AutomationCompletionAction.Shutdown or AutomationCompletionAction.Hibernate or AutomationCompletionAction.Restart;
    }

    private static bool Valid(FrozenCompletionIntent intent) => intent.RunId != Guid.Empty && intent.AttemptId != Guid.Empty && intent.QueueId != Guid.Empty &&
        intent.QueueRevision is >= 1 and <= 9007199254740991 && Enum.IsDefined(intent.Action) && Digest(intent.PlanDigest) && Digest(intent.SourceReceiptDigest);

    private static bool Digest(string value) => value is { Length: 64 } && value.All(character => character is >= 'a' and <= 'f' or >= '0' and <= '9');
}
