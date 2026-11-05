namespace NexusPipeline.Core.Execution;

public enum EngineOutcome { NotStarted, Running, Succeeded, Failed, Cancelled, Unknown }
public enum BusinessOutcome { Unverified, Succeeded, Partial, Failed, NotApplicable, Cancelled }
public enum LifecycleOutcome { NotStarted, Running, Completed, Cancelled, Interrupted, Blocked, Skipped }
public enum RecoveryOutcome { NotRequired, Pending, Restored, Quarantined }

public sealed record AttemptFacts(
    Guid RunId,
    Guid AttemptId,
    int Ordinal,
    EngineOutcome Engine,
    BusinessOutcome Business,
    LifecycleOutcome Lifecycle,
    RecoveryOutcome Recovery,
    string ReasonCode)
{
    public AttemptFacts AfterInterruption(WriterState writers, bool isConfigurationRestored)
    {
        if (Lifecycle != LifecycleOutcome.Running)
            throw new InvalidOperationException("Only an active attempt may receive interruption facts");
        if (writers != WriterState.Stopped)
            return this with { Recovery = RecoveryOutcome.Quarantined, ReasonCode = "writer_stop_unconfirmed" };
        if (!isConfigurationRestored)
            return this with { Recovery = RecoveryOutcome.Quarantined, ReasonCode = "configuration_not_restored" };
        return this with
        {
            Lifecycle = LifecycleOutcome.Interrupted,
            Recovery = RecoveryOutcome.Restored,
            Engine = Engine is EngineOutcome.Running ? EngineOutcome.Unknown : Engine,
            ReasonCode = "host_interrupted"
        };
    }
}
