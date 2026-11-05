using NexusPipeline.Platform.Execution.Judgement;

namespace NexusPipeline.Platform.Storage.Execution;

internal static class JudgementEvidenceBudget
{
    internal static long MaximumInputBytes(long maximum, long recorded, bool final) =>
        Available(maximum, recorded, final) - JudgementProbeProtocol.FaultReserveBytes;

    internal static long AllocateProbes(long maximum, long recorded, long maximumInputBytes, bool final)
    {
        if (maximumInputBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumInputBytes));
        var available = Available(maximum, recorded, final) - maximumInputBytes;
        return available < JudgementProbeProtocol.FaultReserveBytes ? 0 : Math.Min(available, JudgementProbeProtocol.MaximumEvidenceBytes);
    }

    private static long Available(long maximum, long recorded, bool final)
    {
        if (maximum is < JudgementWorkerProtocol.MaximumInvocationBytes or > JudgementWorkerProtocol.MaximumEvidenceBytes ||
            recorded < 0 || recorded > maximum) throw new IOException("judgement_evidence_capacity_exhausted");
        var finalReserve = final ? 0L : JudgementWorkerProtocol.MaximumInvocationBytes + JudgementWorkerProtocol.MaximumResponseBytes +
            JudgementProbeProtocol.FaultReserveBytes;
        return maximum - recorded - finalReserve - JudgementWorkerProtocol.MaximumResponseBytes;
    }
}
