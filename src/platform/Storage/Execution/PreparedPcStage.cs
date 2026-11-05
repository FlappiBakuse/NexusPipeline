using System.Runtime.Versioning;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Platform.Storage.Execution;

[SupportedOSPlatform("windows")]
internal sealed class PreparedPcStage
{
    private readonly PcTargetStage _owner;
    internal PcStageBinding Binding { get; }
    internal TargetExecutionOutcome Outcome { get; }
    internal string ReasonCode { get; }

    internal PreparedPcStage(PcTargetStage owner, PcStageBinding binding,
        TargetExecutionOutcome outcome, string reasonCode)
    { _owner = owner; Binding = binding; Outcome = outcome; ReasonCode = reasonCode; }

    internal T Use<T>(PcStageBinding expected, Func<PcStageDocument, T> callback) =>
        _owner.UseProof(this, expected, callback);
}
