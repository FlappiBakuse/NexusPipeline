using System.Diagnostics;
using NexusPipeline.Core.Execution;

namespace NexusPipeline.Platform.Storage.Execution;

internal sealed class AndroidControlClock(ExecutionTimeBudget runBudget, TimeSpan cleanupLimit)
{
    private long? _cleanupStarted;

    internal TimeSpan Remaining(bool cleanup)
    {
        if (!cleanup) return runBudget.Read().Remaining;
        if (cleanupLimit <= TimeSpan.Zero || cleanupLimit > TimeSpan.FromMinutes(1))
            throw new IOException("execution_android_cleanup_budget_invalid");
        _cleanupStarted ??= Stopwatch.GetTimestamp();
        var remaining = cleanupLimit - Stopwatch.GetElapsedTime(_cleanupStarted.Value);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    internal void Require(bool cleanup)
    {
        if (Remaining(cleanup) == TimeSpan.Zero)
            throw new IOException(cleanup ? "execution_android_cleanup_deadline" : "execution_total_budget_exhausted");
    }
}
