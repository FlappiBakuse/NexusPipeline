namespace NexusPipeline.Core.Configuration;

public enum EffectGateState { Unentered, Entered, Sealed }
public sealed record EffectGateSnapshot(Guid InstanceId, Guid JournalId, Guid OperationId, JournalKind Kind,
    string PlanDigest, long Revision, EffectGateState State);

public interface IJournalEffectGate
{
    EffectGateSnapshot Read(Guid journalId);
    EffectGateSnapshot Enter(RegisteredJournal expected, long expectedGateRevision);
    EffectGateSnapshot SealUnentered(RegisteredJournal expected, long expectedGateRevision);
}
