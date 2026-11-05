using System.Collections.Immutable;

namespace NexusPipeline.Core.Execution;

public enum TargetCommandKind { Effect, ReadOnlyProbe }
public enum TargetCommandObservation { Unverified, Complete, ExpectedOffline, Incomplete, Contradictory, OwnedHandoff }
public enum TargetCleanupOutcome { NotRequired, Completed, Failed, Unverified }
public enum TargetCleanupVerification { None, Application, DeviceOffline }

public sealed record TargetCleanupFact(TargetCleanupOutcome Outcome, string ReasonCode)
{
    public TargetCleanupVerification Verification { get; init; }
    public ImmutableArray<Guid> ConfirmationCommands { get; init; } = [];
}
public sealed record TargetCommandFact(Guid CommandId, int Sequence, EngineOutcome Engine, string ReasonCode)
{
    public TargetCommandKind Kind { get; init; } = TargetCommandKind.Effect;
    public TargetCommandObservation Observation { get; init; }
}
