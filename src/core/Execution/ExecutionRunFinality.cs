namespace NexusPipeline.Core.Execution;

public sealed record RunFinalityDecision(bool IsFinal, string? DeclineReasonCode);

public interface IAttemptTerminalObserver
{
    void OnCommitted(AttemptReceipt receipt);
}
